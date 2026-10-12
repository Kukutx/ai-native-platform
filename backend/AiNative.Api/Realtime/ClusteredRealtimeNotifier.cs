using System.Text.Json;
using AiNative.Application.Abstractions;
using AiNative.Application.Common;
using AiNative.Application.Realtime;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Realtime;

namespace AiNative.Api.Realtime;

/// <summary>
/// 推送给连在本实例上的客户端，同时广播给其他实例，由它们推送给各自的客户端。
/// 客户端只用 WebSocket 直连（跳过协商），连到哪个实例都能收到完整的推送，负载均衡不需要会话保持。
/// </summary>
internal sealed class ClusteredRealtimeNotifier(SignalRRealtimeNotifier local, IClusterBus bus) : IRealtimeNotifier
{
    public Task ChangedAsync(Guid tenantId, RealtimeChangeDto change, CancellationToken cancellationToken = default)
    {
        bus.Publish(ClusterTopics.RealtimeChange, new RealtimeChangeMessage(tenantId, change));
        return local.ChangedAsync(tenantId, change, cancellationToken);
    }

    public Task RunEventAsync(Guid tenantId, RunEventDto runEvent, CancellationToken cancellationToken = default)
    {
        bus.Publish(ClusterTopics.RealtimeRunEvent, new RealtimeRunEventMessage(tenantId, runEvent));
        return local.RunEventAsync(tenantId, runEvent, cancellationToken);
    }
}

internal sealed record RealtimeChangeMessage(Guid TenantId, RealtimeChangeDto Change);

internal sealed record RealtimeRunEventMessage(Guid TenantId, RunEventDto Event);

internal sealed class RealtimeChangeRelay(SignalRRealtimeNotifier local) : IClusterMessageHandler
{
    public string Topic => ClusterTopics.RealtimeChange;

    public Task HandleAsync(JsonElement payload, CancellationToken cancellationToken)
        => payload.Deserialize<RealtimeChangeMessage>(PlatformJson.Options) is { } message
            ? local.ChangedAsync(message.TenantId, message.Change, cancellationToken)
            : Task.CompletedTask;
}

internal sealed class RealtimeRunEventRelay(SignalRRealtimeNotifier local) : IClusterMessageHandler
{
    public string Topic => ClusterTopics.RealtimeRunEvent;

    public Task HandleAsync(JsonElement payload, CancellationToken cancellationToken)
        => payload.Deserialize<RealtimeRunEventMessage>(PlatformJson.Options) is { } message
            ? local.RunEventAsync(message.TenantId, message.Event, cancellationToken)
            : Task.CompletedTask;
}

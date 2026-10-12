using AiNative.Application.Realtime;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace AiNative.Api.Realtime;

internal sealed class SignalRRealtimeNotifier(IHubContext<RealtimeHub> hub) : IRealtimeNotifier
{
    public Task ChangedAsync(Guid tenantId, RealtimeChangeDto change, CancellationToken cancellationToken = default)
        => hub.Clients.Group(RealtimeHub.TenantGroup(tenantId)).SendAsync(RealtimeMethods.Change, change, cancellationToken);

    public Task RunEventAsync(Guid tenantId, RunEventDto runEvent, CancellationToken cancellationToken = default)
        => hub.Clients.Group(RealtimeHub.RunGroup(runEvent.RunId)).SendAsync(RealtimeMethods.RunEvent, runEvent, cancellationToken);
}

using System.Text.Json;
using AiNative.Application.Abstractions;
using AiNative.Application.Agents;
using AiNative.Application.Workflows;
using AiNative.Infrastructure.Agents;
using AiNative.Infrastructure.Common;
using AiNative.Infrastructure.Workflows;

namespace AiNative.Infrastructure.Cluster;

/// <summary>
/// 唤醒本实例的调度循环，同时通知其他实例：新入队的工作谁有空谁领。
/// 广播做了合并：短时间内的多次通知只发一条，批量操作不会刷屏。
/// </summary>
internal abstract class BroadcastSignal(CoalescingSignal local, IClusterBus bus, string topic, TimeProvider clock)
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(50);
    private int _pending;

    public void Notify()
    {
        local.Notify();
        if (Interlocked.Exchange(ref _pending, 1) == 0)
        {
            _ = PublishLaterAsync();
        }
    }

    private async Task PublishLaterAsync()
    {
        await Task.Delay(Window, clock).ConfigureAwait(false);

        // 先清标记再发：发送期间到来的通知会再安排一次，不会漏掉。
        Volatile.Write(ref _pending, 0);
        bus.Publish(topic, new { });
    }
}

internal sealed class BroadcastWakeupSignal(WakeupSignal local, IClusterBus bus, TimeProvider clock)
    : BroadcastSignal(local, bus, ClusterTopics.WakeupQueued, clock), IWakeupSignal;

internal sealed class BroadcastWorkflowSignal(WorkflowSignal local, IClusterBus bus, TimeProvider clock)
    : BroadcastSignal(local, bus, ClusterTopics.WorkflowQueued, clock), IWorkflowSignal;

/// <summary>
/// 其他实例有新工作入队时唤醒本实例的调度循环。
/// </summary>
internal sealed class SignalRelay(string topic, CoalescingSignal local) : IClusterMessageHandler
{
    public string Topic => topic;

    public Task HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        local.Notify();
        return Task.CompletedTask;
    }
}

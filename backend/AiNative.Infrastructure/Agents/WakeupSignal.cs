using System.Threading.Channels;
using AiNative.Application.Agents;

namespace AiNative.Infrastructure.Agents;

/// <summary>
/// 进程内的"有新唤醒"信号。多次通知合并为一次，调度器醒来后会一次性处理所有排队请求。
/// </summary>
internal sealed class WakeupSignal : IWakeupSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public void Notify() => _channel.Writer.TryWrite(true);

    /// <summary>
    /// 等到有信号或超时；宿主停止时抛出 <see cref="OperationCanceledException"/>。
    /// </summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }
}

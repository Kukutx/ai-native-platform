using System.Threading.Channels;

namespace AiNative.Infrastructure.Common;

/// <summary>
/// 进程内的"有新工作"信号。多次通知合并为一次，后台循环醒来后一次性处理所有积压的工作。
/// </summary>
internal abstract class CoalescingSignal
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

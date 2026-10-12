using AiNative.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AiNative.Infrastructure.Dispatching;

/// <summary>
/// 事务提交后的动作队列（作用域内）。事务进行中先排队，提交后按顺序执行；不在事务中则立即执行。
/// 这些动作都是尽力而为的通知类操作，失败只记日志。
/// </summary>
internal sealed class PostCommitActions(ILogger<PostCommitActions> logger) : IPostCommitActions
{
    private readonly List<Func<CancellationToken, Task>> _queue = [];
    private int _depth;

    public void Enqueue(Func<CancellationToken, Task> action)
    {
        if (_depth > 0)
        {
            _queue.Add(action);
            return;
        }

        _ = RunAsync(action, CancellationToken.None);
    }

    public void Begin() => _depth++;

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (--_depth > 0)
        {
            return;
        }

        var pending = _queue.ToArray();
        _queue.Clear();
        foreach (var action in pending)
        {
            await RunAsync(action, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Abort()
    {
        if (--_depth <= 0)
        {
            _depth = 0;
            _queue.Clear();
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Post-commit action failed");
        }
    }
}

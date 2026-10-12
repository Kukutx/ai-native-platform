namespace AiNative.Application.Abstractions;

/// <summary>
/// 事务提交后才执行的动作（实时推送、唤醒调度器等）。没有外层事务时立即执行。
/// </summary>
public interface IPostCommitActions
{
    void Enqueue(Func<CancellationToken, Task> action);
}

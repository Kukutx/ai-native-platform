using AiNative.Domain.Workflows;

namespace AiNative.Application.Workflows;

/// <summary>
/// 当前作用域是否在执行某个工作流运行的步骤。步骤里的写操作引发事件、再触发工作流时，
/// 新运行据此记录来源和层数，用来阻止循环触发。
/// </summary>
public sealed class WorkflowExecutionScope
{
    public Guid? RunId { get; private set; }

    public Guid? WorkflowId { get; private set; }

    public int Depth { get; private set; }

    public void Enter(WorkflowRun run) => Enter(run.Id, run.WorkflowId, run.Depth);

    public void Enter(Guid runId, Guid workflowId, int depth)
    {
        RunId = runId;
        WorkflowId = workflowId;
        Depth = depth;
    }

    /// <summary>
    /// 由当前作用域引发的新运行所处的层数。
    /// </summary>
    public int ChildDepth => RunId is null ? 0 : Depth + 1;
}

/// <summary>
/// 通知工作流引擎有新的运行可以执行（进程内信号，省去轮询等待）。
/// </summary>
public interface IWorkflowSignal
{
    void Notify();
}

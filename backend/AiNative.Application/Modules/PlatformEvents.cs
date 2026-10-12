namespace AiNative.Application.Modules;

/// <summary>
/// 平台事件定义。Description 写给人和模型看：什么时候发生、载荷里有哪些字段（工作流表达式里通过 trigger.xxx 引用）。
/// 订阅事件的工作流所有者必须拥有 RequiredFeature，载荷里的数据不会因此越权外泄。
/// </summary>
public sealed record PlatformEventDefinition(string Key, string Name, string ModuleKey, string RequiredFeature, string Description);

/// <summary>
/// 发布平台事件。必须在产生事件的写操作所在事务内调用：订阅它的工作流运行随事务一起提交，事务回滚则什么都不会发生。
/// </summary>
public interface IPlatformEvents
{
    /// <param name="eventType">事件键，必须是某个模块声明过的事件。</param>
    /// <param name="subjectId">事件主体（例如任务、审批的 ID），用于唤醒正在等它的工作流运行。</param>
    /// <param name="payload">载荷工厂：只有确实有工作流订阅时才会调用，避免无谓的查询。</param>
    Task PublishAsync(string eventType, Guid subjectId, Func<Task<object>> payload, CancellationToken cancellationToken = default);
}

/// <summary>
/// 内置模块发布的事件键。
/// </summary>
public static class PlatformEventKeys
{
    public const string TaskCreated = "tasks.created";
    public const string TaskStatusChanged = "tasks.status_changed";
    public const string TaskCommented = "tasks.commented";
    public const string ApprovalDecided = "approvals.decided";
}

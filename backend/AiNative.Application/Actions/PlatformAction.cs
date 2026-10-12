using AiNative.Domain.Common;

namespace AiNative.Application.Actions;

public enum ApprovalPolicy
{
    /// <summary>
    /// 有权限即可直接执行。
    /// </summary>
    None = 0,

    /// <summary>
    /// 智能体发起时必须人工审批；人直接操作不受影响。
    /// </summary>
    RequiredForAgents = 1,
}

/// <summary>
/// 动作执行时的上下文。身份与权限来自 ITenantContext，这里只放与本次调用相关的附加信息。
/// </summary>
public sealed record ActionContext(Actor Actor, Guid? RunId = null, Guid? ApprovalId = null);

/// <summary>
/// 平台动作：一次定义，同时供工作台界面、智能体工具调用、HTTP 与 MCP 使用。
/// 输入类型自动生成 JSON Schema，权限和审批策略在调用入口统一检查。
/// </summary>
public interface IPlatformAction
{
    /// <summary>
    /// 全局唯一键，形如 "tasks.create"。
    /// </summary>
    string Key { get; }

    string ModuleKey { get; }

    string Title { get; }

    /// <summary>
    /// 给模型看的说明：做什么、何时用、注意事项。
    /// </summary>
    string Description { get; }

    string RequiredFeature { get; }

    bool IsMutation { get; }

    ApprovalPolicy ApprovalPolicy { get; }

    Type InputType { get; }

    Task<object?> ExecuteAsync(object input, ActionContext context, CancellationToken cancellationToken);
}

public abstract class PlatformAction<TInput, TOutput> : IPlatformAction
    where TInput : class
{
    public abstract string Key { get; }

    public abstract string ModuleKey { get; }

    public abstract string Title { get; }

    public abstract string Description { get; }

    public abstract string RequiredFeature { get; }

    public virtual bool IsMutation => true;

    public virtual ApprovalPolicy ApprovalPolicy => ApprovalPolicy.None;

    public Type InputType => typeof(TInput);

    async Task<object?> IPlatformAction.ExecuteAsync(object input, ActionContext context, CancellationToken cancellationToken)
        => await ExecuteAsync((TInput)input, context, cancellationToken).ConfigureAwait(false);

    protected abstract Task<TOutput> ExecuteAsync(TInput input, ActionContext context, CancellationToken cancellationToken);
}

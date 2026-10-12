using System.Text.Json;
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
public sealed record ActionContext(Actor Actor, Guid? RunId = null, Guid? ApprovalId = null, Guid? WorkItemId = null);

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

/// <summary>
/// 运行时才知道的动作（例如外部 MCP 服务提供的工具）：由 <see cref="IDynamicActionSource"/> 按租户产生，
/// 自带输入 Schema，输入以 JsonElement 原样传入。不参与程序集扫描。
/// </summary>
public interface IDynamicPlatformAction : IPlatformAction
{
    JsonElement InputSchema { get; }
}

/// <summary>
/// 动态动作的来源。实现类放进已注册的程序集即可被扫描（按作用域注册）；同一作用域内应缓存结果。
/// </summary>
public interface IDynamicActionSource
{
    /// <summary>
    /// 产生的动作所属模块；模块停用时不会来取。
    /// </summary>
    string ModuleKey { get; }

    /// <summary>
    /// 动作键是否归这个来源管（按前缀判断，不查库），查找内置动作时据此跳过无关的来源。
    /// </summary>
    bool Owns(string actionKey);

    /// <summary>
    /// 当前租户的全部动态动作（不按权限过滤）。
    /// </summary>
    Task<IReadOnlyList<IDynamicPlatformAction>> GetActionsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 按键查找一个动态动作。执行单次工具调用时只需要它，实现可以只加载相关的那部分。
    /// </summary>
    Task<IDynamicPlatformAction?> FindAsync(string actionKey, CancellationToken cancellationToken);
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

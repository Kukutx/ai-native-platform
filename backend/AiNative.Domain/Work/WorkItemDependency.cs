using AiNative.Domain.Common;

namespace AiNative.Domain.Work;

/// <summary>
/// 任务依赖（完成后才能开始）：<see cref="WorkItemId"/> 要等 <see cref="DependsOnId"/> 关闭（完成或取消）后才能开始。
/// 前置任务未关闭时，负责的智能体不会领取后续任务；依赖关系不能形成循环（由应用层在锁内检查）。
/// </summary>
public sealed class WorkItemDependency : TenantEntity
{
    public Guid WorkItemId { get; set; }

    public Guid DependsOnId { get; set; }
}

using AiNative.Domain.Common;

namespace AiNative.Domain.Tenancy;

/// <summary>
/// 租户：数据与权限的最外层边界（个人空间、团队或企业）。
/// </summary>
public sealed class Tenant : Entity
{
    public required string Name { get; set; }

    public required string Slug { get; set; }

    /// <summary>
    /// 工作项编号计数器，按租户递增（T-1、T-2……），通过原子 UPDATE 分配。
    /// </summary>
    public int WorkItemSequence { get; set; }
}

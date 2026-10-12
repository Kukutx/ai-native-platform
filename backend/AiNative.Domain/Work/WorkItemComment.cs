using AiNative.Domain.Common;

namespace AiNative.Domain.Work;

/// <summary>
/// 工作项上的讨论。智能体之间、人与智能体之间的协作都通过评论留痕。
/// </summary>
public sealed class WorkItemComment : TenantEntity
{
    public Guid WorkItemId { get; set; }

    public Actor Author { get; set; }

    public required string Body { get; set; }

    public Guid? RunId { get; set; }
}

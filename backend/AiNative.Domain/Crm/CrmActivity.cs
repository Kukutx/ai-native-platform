using AiNative.Domain.Common;

namespace AiNative.Domain.Crm;

public enum CrmActivityKind
{
    Note = 0,
    Call = 1,
    Meeting = 2,
    Email = 3,

    /// <summary>
    /// 商机阶段变化，由系统自动记录。
    /// </summary>
    StageChange = 4,
}

/// <summary>
/// 跟进记录：一次电话、会议、邮件或备注，可以同时关联客户、联系人和商机。
/// 记在联系人或商机上时自动带上它们所属的客户，客户的时间线因此能看到全部往来。
/// </summary>
public sealed class CrmActivity : TenantEntity
{
    public CrmActivityKind Kind { get; set; }

    public required string Content { get; set; }

    /// <summary>
    /// 实际发生的时间（补记以前的通话时早于 CreatedAt）。
    /// </summary>
    public DateTimeOffset OccurredAt { get; set; }

    public Guid? CompanyId { get; set; }

    public Guid? ContactId { get; set; }

    public Guid? DealId { get; set; }

    public Actor Author { get; set; }

    /// <summary>
    /// 智能体记录时所在的执行轮次。
    /// </summary>
    public Guid? RunId { get; set; }
}

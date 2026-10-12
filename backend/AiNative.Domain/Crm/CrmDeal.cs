using AiNative.Domain.Common;

namespace AiNative.Domain.Crm;

public enum CrmStageKind
{
    Open = 0,
    Won = 1,
    Lost = 2,
}

public enum CrmDealStatus
{
    Open = 0,
    Won = 1,
    Lost = 2,
}

/// <summary>
/// 销售阶段。每个空间自己配置，按 Position 排序；赢单、输单阶段让商机结束。
/// </summary>
public sealed class CrmStage : TenantEntity
{
    public required string Name { get; set; }

    public int Position { get; set; }

    /// <summary>
    /// 处于该阶段的商机成交概率（0–100），用于加权预测。
    /// </summary>
    public int Probability { get; set; }

    public CrmStageKind Kind { get; set; }
}

/// <summary>
/// 商机：一笔可能成交的生意，沿销售阶段推进直到赢单或输单。
/// </summary>
public sealed class CrmDeal : TenantEntity
{
    public required string Title { get; set; }

    public Guid? CompanyId { get; set; }

    public Guid? ContactId { get; set; }

    public Guid StageId { get; set; }

    /// <summary>
    /// 由所在阶段决定，冗余存储便于按状态查询。
    /// </summary>
    public CrmDealStatus Status { get; private set; }

    public decimal? Amount { get; set; }

    /// <summary>
    /// ISO 4217 货币代码，例如 CNY、USD。
    /// </summary>
    public string Currency { get; set; } = "CNY";

    public DateOnly? ExpectedCloseDate { get; set; }

    public string Description { get; set; } = string.Empty;

    public Guid? OwnerUserId { get; set; }

    public Actor CreatedBy { get; set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public DateTimeOffset? LastActivityAt { get; set; }

    /// <summary>
    /// 移到某个阶段：状态跟随阶段类型；进入赢单或输单时记下结束时间，回到进行中的阶段则清除。
    /// </summary>
    public void MoveTo(CrmStage stage, DateTimeOffset now)
    {
        var status = stage.Kind switch
        {
            CrmStageKind.Won => CrmDealStatus.Won,
            CrmStageKind.Lost => CrmDealStatus.Lost,
            _ => CrmDealStatus.Open,
        };
        if (status != Status || StageId == Guid.Empty)
        {
            ClosedAt = status == CrmDealStatus.Open ? null : now;
        }

        StageId = stage.Id;
        Status = status;
    }
}

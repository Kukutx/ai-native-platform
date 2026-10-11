using AiNative.Contracts.Common;
using AiNative.Domain.Crm;

namespace AiNative.Contracts.Crm;

public sealed record CrmCompanyDto(
    Guid Id,
    string Name,
    string Industry,
    string Website,
    string Phone,
    string Address,
    string Description,
    IReadOnlyList<string> Tags,
    ActorDto? Owner,
    int ContactCount,
    int OpenDealCount,
    ActorDto CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastActivityAt);

public sealed record CrmContactDto(
    Guid Id,
    string Name,
    Guid? CompanyId,
    string? CompanyName,
    string Title,
    string Email,
    string Phone,
    string Description,
    IReadOnlyList<string> Tags,
    ActorDto? Owner,
    ActorDto CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastActivityAt);

public sealed record CrmStageDto(Guid Id, string Name, int Position, int Probability, CrmStageKind Kind);

public sealed record CrmDealDto(
    Guid Id,
    string Title,
    Guid? CompanyId,
    string? CompanyName,
    Guid? ContactId,
    string? ContactName,
    Guid StageId,
    string StageName,
    CrmDealStatus Status,
    int Probability,
    decimal? Amount,
    string Currency,
    DateOnly? ExpectedCloseDate,
    string Description,
    ActorDto? Owner,
    ActorDto CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClosedAt,
    DateTimeOffset? LastActivityAt);

public sealed record CrmActivityDto(
    Guid Id,
    CrmActivityKind Kind,
    string Content,
    DateTimeOffset OccurredAt,
    Guid? CompanyId,
    string? CompanyName,
    Guid? ContactId,
    string? ContactName,
    Guid? DealId,
    string? DealTitle,
    ActorDto Author,
    DateTimeOffset CreatedAt);

public sealed record CrmCompanyDetailDto(
    CrmCompanyDto Company,
    IReadOnlyList<CrmContactDto> Contacts,
    IReadOnlyList<CrmDealDto> Deals,
    IReadOnlyList<CrmActivityDto> Activities);

public sealed record CrmContactDetailDto(
    CrmContactDto Contact,
    IReadOnlyList<CrmDealDto> Deals,
    IReadOnlyList<CrmActivityDto> Activities);

public sealed record CrmDealDetailDto(CrmDealDto Deal, IReadOnlyList<CrmActivityDto> Activities);

/// <summary>
/// 同一货币的金额合计。不同货币不做换算，分别列出。
/// </summary>
public sealed record CrmAmountDto(string Currency, decimal Amount);

/// <summary>
/// 看板上的一列：进行中的阶段统计全部商机，赢单、输单阶段只统计最近 <see cref="CrmPipelineDto.ClosedWindowDays"/> 天结束的。
/// </summary>
public sealed record CrmPipelineStageDto(
    CrmStageDto Stage,
    int DealCount,
    IReadOnlyList<CrmAmountDto> Totals,
    IReadOnlyList<CrmDealDto> Deals);

public sealed record CrmPipelineDto(
    IReadOnlyList<CrmPipelineStageDto> Stages,
    IReadOnlyList<CrmAmountDto> OpenTotals,
    IReadOnlyList<CrmAmountDto> WeightedTotals,
    IReadOnlyList<CrmAmountDto> WonThisMonth,
    int ClosedWindowDays);

public sealed record SaveCrmCompanyRequest
{
    public required string Name { get; init; }

    public string Industry { get; init; } = string.Empty;

    public string Website { get; init; } = string.Empty;

    public string Phone { get; init; } = string.Empty;

    public string Address { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public IReadOnlyList<string> Tags { get; init; } = [];

    public Guid? OwnerUserId { get; init; }

    /// <summary>
    /// 编辑时回传读取到的 UpdatedAt；期间被别人改过则返回 409。
    /// </summary>
    public DateTimeOffset? ExpectedUpdatedAt { get; init; }
}

public sealed record SaveCrmContactRequest
{
    public required string Name { get; init; }

    public Guid? CompanyId { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string Phone { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public IReadOnlyList<string> Tags { get; init; } = [];

    public Guid? OwnerUserId { get; init; }

    public DateTimeOffset? ExpectedUpdatedAt { get; init; }
}

public sealed record SaveCrmDealRequest
{
    public required string Title { get; init; }

    public Guid? CompanyId { get; init; }

    public Guid? ContactId { get; init; }

    /// <summary>
    /// 所在阶段；新建时留空放进第一个进行中的阶段。修改阶段与拖动看板效果相同（记录阶段变化并触发事件）。
    /// </summary>
    public Guid? StageId { get; init; }

    public decimal? Amount { get; init; }

    public string Currency { get; init; } = "CNY";

    public DateOnly? ExpectedCloseDate { get; init; }

    public string Description { get; init; } = string.Empty;

    public Guid? OwnerUserId { get; init; }

    public DateTimeOffset? ExpectedUpdatedAt { get; init; }
}

public sealed record MoveCrmDealRequest
{
    public required Guid StageId { get; init; }

    /// <summary>
    /// 推进说明（例如赢单原因、输单原因），记入跟进记录。
    /// </summary>
    public string? Note { get; init; }
}

public sealed record LogCrmActivityRequest
{
    public required CrmActivityKind Kind { get; init; }

    public required string Content { get; init; }

    /// <summary>
    /// 实际发生时间，留空为现在。
    /// </summary>
    public DateTimeOffset? OccurredAt { get; init; }

    public Guid? CompanyId { get; init; }

    public Guid? ContactId { get; init; }

    public Guid? DealId { get; init; }
}

public sealed record SaveCrmStageItem
{
    /// <summary>
    /// 已有阶段的 ID；新增阶段留空。
    /// </summary>
    public Guid? Id { get; init; }

    public required string Name { get; init; }

    public int Probability { get; init; }

    public CrmStageKind Kind { get; init; }
}

/// <summary>
/// 按顺序提交完整的阶段列表：没出现的已有阶段会被删除（其中不能还有商机）。
/// </summary>
public sealed record SaveCrmStagesRequest
{
    public required IReadOnlyList<SaveCrmStageItem> Stages { get; init; }
}

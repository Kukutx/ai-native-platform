using System.Text.Json;
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
    DateTimeOffset? LastActivityAt,
    IReadOnlyDictionary<string, JsonElement> CustomFields);

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
    DateTimeOffset? LastActivityAt,
    IReadOnlyDictionary<string, JsonElement> CustomFields);

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
    DateTimeOffset? LastActivityAt,
    IReadOnlyDictionary<string, JsonElement> CustomFields);

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

/// <summary>
/// 客户详情。联系人、商机各最多附带 200 个，ContactTotal、DealTotal 为实际总数，大于列表长度说明列表被截断。
/// </summary>
public sealed record CrmCompanyDetailDto(
    CrmCompanyDto Company,
    IReadOnlyList<CrmContactDto> Contacts,
    IReadOnlyList<CrmDealDto> Deals,
    IReadOnlyList<CrmActivityDto> Activities,
    int ContactTotal,
    int DealTotal);

/// <summary>
/// 联系人详情。商机最多附带 200 个，DealTotal 为实际总数。
/// </summary>
public sealed record CrmContactDetailDto(
    CrmContactDto Contact,
    IReadOnlyList<CrmDealDto> Deals,
    IReadOnlyList<CrmActivityDto> Activities,
    int DealTotal);

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
    /// 自定义字段的值，键为字段标识；不传表示不修改，传了就整体替换（没传的字段被清空）。
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement>? CustomFields { get; init; }

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

    /// <summary>
    /// 自定义字段的值，键为字段标识；不传表示不修改，传了就整体替换（没传的字段被清空）。
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement>? CustomFields { get; init; }

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

    /// <summary>
    /// 自定义字段的值，键为字段标识；不传表示不修改，传了就整体替换（没传的字段被清空）。
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement>? CustomFields { get; init; }

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

public sealed record CrmFieldDto(
    Guid Id,
    CrmRecordType RecordType,
    string Key,
    string Label,
    CrmFieldType Type,
    IReadOnlyList<string> Options,
    bool Required,
    int Position,
    string Description);

public sealed record SaveCrmFieldRequest
{
    public CrmRecordType RecordType { get; init; }

    /// <summary>
    /// 字段标识，创建后不能修改。小写字母开头，由小写字母、数字和下划线组成，例如 region。
    /// </summary>
    public required string Key { get; init; }

    public required string Label { get; init; }

    /// <summary>
    /// 创建后不能修改。
    /// </summary>
    public CrmFieldType Type { get; init; }

    /// <summary>
    /// 单选、多选的选项。
    /// </summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>
    /// 新建记录时必须填写（是否类字段不能设为必填）。
    /// </summary>
    public bool Required { get; init; }

    public string Description { get; init; } = string.Empty;
}

public sealed record ReorderCrmFieldsRequest
{
    public CrmRecordType RecordType { get; init; }

    /// <summary>
    /// 这类记录的全部字段 ID，按新的顺序排列。
    /// </summary>
    public required IReadOnlyList<Guid> Ids { get; init; }
}

/// <summary>
/// 导入时可以对应的字段。
/// </summary>
/// <param name="Key">标准字段为 name、email 这样的名称，自定义字段为 custom.字段标识。</param>
public sealed record CrmImportTargetDto(string Key, string Label, bool Required, string? Hint);

/// <param name="Samples">前几行的值，帮助确认这一列是什么。</param>
/// <param name="SuggestedTarget">按表头猜出的对应字段，猜不出时为空。</param>
public sealed record CrmImportColumnDto(int Index, string Header, IReadOnlyList<string> Samples, string? SuggestedTarget);

public sealed record CrmImportPreviewDto(
    CrmRecordType RecordType,
    string FileName,
    int RowCount,
    IReadOnlyList<CrmImportColumnDto> Columns,
    IReadOnlyList<CrmImportTargetDto> Targets);

/// <summary>
/// 已经有同样的记录时怎么办：客户按名称、联系人按邮箱判断。
/// </summary>
public enum CrmImportDuplicateMode
{
    /// <summary>
    /// 跳过这一行。
    /// </summary>
    Skip = 0,

    /// <summary>
    /// 用这一行里有值的列更新已有记录。
    /// </summary>
    Update = 1,

    /// <summary>
    /// 仍然新建一条。
    /// </summary>
    Create = 2,
}

public sealed record CrmImportOptions
{
    public CrmRecordType RecordType { get; init; }

    /// <summary>
    /// 每一列导入到哪个字段（<see cref="CrmImportTargetDto.Key"/>），顺序与预览返回的列一致；不导入的列为 null 或空字符串。
    /// </summary>
    public IReadOnlyList<string?> Mapping { get; init; } = [];

    public CrmImportDuplicateMode Duplicates { get; init; } = CrmImportDuplicateMode.Skip;

    /// <summary>
    /// 导入联系人时，所属客户不存在就新建。
    /// </summary>
    public bool CreateMissingCompanies { get; init; }

    /// <summary>
    /// 只检查，不保存。
    /// </summary>
    public bool DryRun { get; init; }
}

/// <param name="Row">表格里的行号（表头所在行之后从实际行号算起）。</param>
public sealed record CrmImportRowError(int Row, string Message);

/// <param name="Errors">出错的行，最多列出 200 条；Failed 为实际出错的行数。</param>
public sealed record CrmImportResultDto(
    bool DryRun,
    int Total,
    int Created,
    int Updated,
    int Skipped,
    int Failed,
    int CompaniesCreated,
    IReadOnlyList<CrmImportRowError> Errors);

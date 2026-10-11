using System.ComponentModel;
using AiNative.Application.Abstractions;
using AiNative.Application.Actions;
using AiNative.Application.Common;
using AiNative.Contracts.Crm;
using AiNative.Domain.Crm;

namespace AiNative.Application.Crm.Actions;

public sealed class SearchCrmInput
{
    [Description("关键字：客户名称、联系人姓名、邮箱、电话或商机名称的一部分")]
    public required string Query { get; init; }

    [Description("每类最多返回条数，默认 10")]
    public int Limit { get; init; } = 10;
}

public sealed record CrmSearchResult(IReadOnlyList<CrmCompanyDto> Companies, IReadOnlyList<CrmContactDto> Contacts, IReadOnlyList<CrmDealDto> Deals);

internal sealed class SearchCrmAction(IRequestDispatcher dispatcher) : PlatformAction<SearchCrmInput, CrmSearchResult>
{
    public override string Key => "crm.search";

    public override string ModuleKey => CrmModule.ModuleKey;

    public override string Title => "搜索客户";

    public override string Description => "按关键字同时搜索客户、联系人和商机。录入新客户或联系人之前先搜一下，避免重复；处理与某个客户相关的事情前先用它找到 ID。";

    public override string RequiredFeature => CrmFeatures.View;

    public override bool IsMutation => false;

    protected override async Task<CrmSearchResult> ExecuteAsync(SearchCrmInput input, ActionContext context, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(input.Limit, 1, 50);
        var companies = await dispatcher.SendAsync(new ListCrmCompaniesQuery(input.Query, PageSize: limit), cancellationToken).ConfigureAwait(false);
        var contacts = await dispatcher.SendAsync(new ListCrmContactsQuery(input.Query, PageSize: limit), cancellationToken).ConfigureAwait(false);
        var deals = await dispatcher.SendAsync(new ListCrmDealsQuery(input.Query, PageSize: limit), cancellationToken).ConfigureAwait(false);
        return new CrmSearchResult(companies.Items, contacts.Items, deals.Items);
    }
}

public sealed class GetCrmCompanyInput
{
    [Description("客户 ID")]
    public required Guid CompanyId { get; init; }
}

internal sealed class GetCrmCompanyAction(IRequestDispatcher dispatcher) : PlatformAction<GetCrmCompanyInput, CrmCompanyDetailDto>
{
    public override string Key => "crm.get_company";

    public override string ModuleKey => CrmModule.ModuleKey;

    public override string Title => "查看客户";

    public override string Description => "查看客户详情：基本信息、全部联系人、商机，以及最近 50 条跟进记录（含名下联系人和商机的往来）。";

    public override string RequiredFeature => CrmFeatures.View;

    public override bool IsMutation => false;

    protected override Task<CrmCompanyDetailDto> ExecuteAsync(GetCrmCompanyInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new GetCrmCompanyQuery(input.CompanyId), cancellationToken);
}

public sealed class GetCrmContactInput
{
    [Description("联系人 ID")]
    public required Guid ContactId { get; init; }
}

internal sealed class GetCrmContactAction(IRequestDispatcher dispatcher) : PlatformAction<GetCrmContactInput, CrmContactDetailDto>
{
    public override string Key => "crm.get_contact";

    public override string ModuleKey => CrmModule.ModuleKey;

    public override string Title => "查看联系人";

    public override string Description => "查看联系人详情：所属客户、联系方式、相关商机和最近 50 条跟进记录。";

    public override string RequiredFeature => CrmFeatures.View;

    public override bool IsMutation => false;

    protected override Task<CrmContactDetailDto> ExecuteAsync(GetCrmContactInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new GetCrmContactQuery(input.ContactId), cancellationToken);
}

public sealed class GetCrmDealInput
{
    [Description("商机 ID")]
    public required Guid DealId { get; init; }
}

internal sealed class GetCrmDealAction(IRequestDispatcher dispatcher) : PlatformAction<GetCrmDealInput, CrmDealDetailDto>
{
    public override string Key => "crm.get_deal";

    public override string ModuleKey => CrmModule.ModuleKey;

    public override string Title => "查看商机";

    public override string Description => "查看商机详情：客户、联系人、所在阶段、金额、预计成交日期，以及阶段变化和跟进记录。";

    public override string RequiredFeature => CrmFeatures.View;

    public override bool IsMutation => false;

    protected override Task<CrmDealDetailDto> ExecuteAsync(GetCrmDealInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new GetCrmDealQuery(input.DealId), cancellationToken);
}

public sealed class ListCrmDealsInput
{
    [Description("按状态过滤：open 进行中、won 赢单、lost 输单")]
    public CrmDealStatus? Status { get; init; }

    [Description("按阶段名称过滤，例如「商务谈判」")]
    public string? StageName { get; init; }

    [Description("只看某个客户的商机")]
    public Guid? CompanyId { get; init; }

    [Description("只看某个成员负责的商机（成员的用户 ID）")]
    public Guid? OwnerUserId { get; init; }

    [Description("商机名称关键字")]
    public string? Search { get; init; }

    [Description("最多返回条数，默认 20")]
    public int Limit { get; init; } = 20;
}

internal sealed class ListCrmDealsAction(IRequestDispatcher dispatcher) : PlatformAction<ListCrmDealsInput, IReadOnlyList<CrmDealDto>>
{
    public override string Key => "crm.list_deals";

    public override string ModuleKey => CrmModule.ModuleKey;

    public override string Title => "查询商机";

    public override string Description => "按状态、阶段、客户或负责人查询商机，最近更新的在前。";

    public override string RequiredFeature => CrmFeatures.View;

    public override bool IsMutation => false;

    protected override async Task<IReadOnlyList<CrmDealDto>> ExecuteAsync(ListCrmDealsInput input, ActionContext context, CancellationToken cancellationToken)
    {
        Guid? stageId = string.IsNullOrWhiteSpace(input.StageName)
            ? null
            : (await CrmStageNames.ResolveAsync(dispatcher, input.StageName, "stageName", cancellationToken).ConfigureAwait(false)).Id;
        var result = await dispatcher.SendAsync(
            new ListCrmDealsQuery(input.Search, stageId, input.Status, input.CompanyId, null, input.OwnerUserId, 1, Math.Clamp(input.Limit, 1, 100)),
            cancellationToken).ConfigureAwait(false);
        return result.Items;
    }
}

public sealed class GetCrmPipelineInput
{
    [Description("只统计某个成员负责的商机；留空统计全部")]
    public Guid? OwnerUserId { get; init; }
}

public sealed record CrmPipelineStageSummary(string Name, CrmStageKind Kind, int Probability, int DealCount, IReadOnlyList<CrmAmountDto> Totals);

public sealed record CrmPipelineSummary(
    IReadOnlyList<CrmPipelineStageSummary> Stages,
    IReadOnlyList<CrmAmountDto> OpenTotals,
    IReadOnlyList<CrmAmountDto> WeightedTotals,
    IReadOnlyList<CrmAmountDto> WonThisMonth,
    string Note);

internal sealed class GetCrmPipelineAction(IRequestDispatcher dispatcher) : PlatformAction<GetCrmPipelineInput, CrmPipelineSummary>
{
    public override string Key => "crm.get_pipeline";

    public override string ModuleKey => CrmModule.ModuleKey;

    public override string Title => "销售漏斗概况";

    public override string Description => "销售漏斗概况：每个阶段的商机数量与金额、进行中商机总额、按阶段概率加权的预测金额、本月赢单金额。回答「管道情况」「这个月业绩」这类问题时使用。";

    public override string RequiredFeature => CrmFeatures.View;

    public override bool IsMutation => false;

    protected override async Task<CrmPipelineSummary> ExecuteAsync(GetCrmPipelineInput input, ActionContext context, CancellationToken cancellationToken)
    {
        var pipeline = await dispatcher.SendAsync(new GetCrmPipelineQuery(input.OwnerUserId), cancellationToken).ConfigureAwait(false);
        return new CrmPipelineSummary(
            [.. pipeline.Stages.Select(s => new CrmPipelineStageSummary(s.Stage.Name, s.Stage.Kind, s.Stage.Probability, s.DealCount, s.Totals))],
            pipeline.OpenTotals,
            pipeline.WeightedTotals,
            pipeline.WonThisMonth,
            $"赢单、输单阶段只统计最近 {pipeline.ClosedWindowDays} 天结束的商机；不同货币分别合计，不做换算。");
    }
}

public sealed class CreateCrmCompanyInput
{
    [Description("客户名称（公司或组织全称）")]
    public required string Name { get; init; }

    [Description("行业")]
    public string Industry { get; init; } = string.Empty;

    [Description("官网")]
    public string Website { get; init; } = string.Empty;

    [Description("电话")]
    public string Phone { get; init; } = string.Empty;

    [Description("地址")]
    public string Address { get; init; } = string.Empty;

    [Description("简介：业务、规模、合作背景等")]
    public string Description { get; init; } = string.Empty;

    [Description("标签，例如 [\"重点客户\", \"华东\"]")]
    public List<string> Tags { get; init; } = [];

    [Description("负责的成员（用户 ID）；留空时由创建者负责，智能体创建时为空")]
    public Guid? OwnerUserId { get; init; }
}

internal sealed class CreateCrmCompanyAction(IRequestDispatcher dispatcher) : PlatformAction<CreateCrmCompanyInput, CrmCompanyDto>
{
    public override string Key => "crm.create_company";

    public override string ModuleKey => CrmModule.ModuleKey;

    public override string Title => "新建客户";

    public override string Description => "新建客户（公司或组织）。新建前先用 crm.search 确认还没有这个客户。";

    public override string RequiredFeature => CrmFeatures.Manage;

    protected override Task<CrmCompanyDto> ExecuteAsync(CreateCrmCompanyInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new CreateCrmCompanyCommand(new SaveCrmCompanyRequest
        {
            Name = input.Name,
            Industry = input.Industry,
            Website = input.Website,
            Phone = input.Phone,
            Address = input.Address,
            Description = input.Description,
            Tags = input.Tags,
            OwnerUserId = input.OwnerUserId,
        }), cancellationToken);
}

public sealed class UpdateCrmCompanyInput
{
    [Description("客户 ID")]
    public required Guid CompanyId { get; init; }

    [Description("新名称；不改就省略")]
    public string? Name { get; init; }

    [Description("行业；不改就省略，传空字符串表示清空")]
    public string? Industry { get; init; }

    [Description("官网；不改就省略")]
    public string? Website { get; init; }

    [Description("电话；不改就省略")]
    public string? Phone { get; init; }

    [Description("地址；不改就省略")]
    public string? Address { get; init; }

    [Description("简介（整体替换）；不改就省略")]
    public string? Description { get; init; }

    [Description("标签（整体替换）；不改就省略")]
    public List<string>? Tags { get; init; }

    [Description("改由哪个成员负责（用户 ID）；不改就省略")]
    public Guid? OwnerUserId { get; init; }
}

internal sealed class UpdateCrmCompanyAction(IRequestDispatcher dispatcher) : PlatformAction<UpdateCrmCompanyInput, CrmCompanyDto>
{
    public override string Key => "crm.update_company";

    public override string ModuleKey => CrmModule.ModuleKey;

    public override string Title => "修改客户";

    public override string Description => "修改客户信息，只需要传要改的字段。";

    public override string RequiredFeature => CrmFeatures.Manage;

    protected override async Task<CrmCompanyDto> ExecuteAsync(UpdateCrmCompanyInput input, ActionContext context, CancellationToken cancellationToken)
    {
        var current = (await dispatcher.SendAsync(new GetCrmCompanyQuery(input.CompanyId), cancellationToken).ConfigureAwait(false)).Company;
        return await dispatcher.SendAsync(new UpdateCrmCompanyCommand(current.Id, new SaveCrmCompanyRequest
        {
            Name = input.Name ?? current.Name,
            Industry = input.Industry ?? current.Industry,
            Website = input.Website ?? current.Website,
            Phone = input.Phone ?? current.Phone,
            Address = input.Address ?? current.Address,
            Description = input.Description ?? current.Description,
            Tags = input.Tags ?? current.Tags,
            OwnerUserId = input.OwnerUserId ?? current.Owner?.Id,
            ExpectedUpdatedAt = current.UpdatedAt,
        }), cancellationToken).ConfigureAwait(false);
    }
}

public sealed class CreateCrmContactInput
{
    [Description("姓名")]
    public required string Name { get; init; }

    [Description("所属客户 ID；个人客户或暂不归档时省略")]
    public Guid? CompanyId { get; init; }

    [Description("职位")]
    public string Title { get; init; } = string.Empty;

    [Description("邮箱")]
    public string Email { get; init; } = string.Empty;

    [Description("电话")]
    public string Phone { get; init; } = string.Empty;

    [Description("备注：负责什么、偏好、关系背景等")]
    public string Description { get; init; } = string.Empty;

    [Description("标签")]
    public List<string> Tags { get; init; } = [];

    [Description("负责的成员（用户 ID）；留空时由创建者负责，智能体创建时为空")]
    public Guid? OwnerUserId { get; init; }
}

internal sealed class CreateCrmContactAction(IRequestDispatcher dispatcher) : PlatformAction<CreateCrmContactInput, CrmContactDto>
{
    public override string Key => "crm.create_contact";

    public override string ModuleKey => CrmModule.ModuleKey;

    public override string Title => "新建联系人";

    public override string Description => "新建联系人，可以挂在某个客户下。新建前先用 crm.search 确认还没有这个人；所属客户不存在时先用 crm.create_company 建客户。";

    public override string RequiredFeature => CrmFeatures.Manage;

    protected override Task<CrmContactDto> ExecuteAsync(CreateCrmContactInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new CreateCrmContactCommand(new SaveCrmContactRequest
        {
            Name = input.Name,
            CompanyId = input.CompanyId,
            Title = input.Title,
            Email = input.Email,
            Phone = input.Phone,
            Description = input.Description,
            Tags = input.Tags,
            OwnerUserId = input.OwnerUserId,
        }), cancellationToken);
}

public sealed class UpdateCrmContactInput
{
    [Description("联系人 ID")]
    public required Guid ContactId { get; init; }

    [Description("新姓名；不改就省略")]
    public string? Name { get; init; }

    [Description("改挂到哪个客户下；不改就省略")]
    public Guid? CompanyId { get; init; }

    [Description("职位；不改就省略，传空字符串表示清空")]
    public string? Title { get; init; }

    [Description("邮箱；不改就省略")]
    public string? Email { get; init; }

    [Description("电话；不改就省略")]
    public string? Phone { get; init; }

    [Description("备注（整体替换）；不改就省略")]
    public string? Description { get; init; }

    [Description("标签（整体替换）；不改就省略")]
    public List<string>? Tags { get; init; }

    [Description("改由哪个成员负责（用户 ID）；不改就省略")]
    public Guid? OwnerUserId { get; init; }
}

internal sealed class UpdateCrmContactAction(IRequestDispatcher dispatcher) : PlatformAction<UpdateCrmContactInput, CrmContactDto>
{
    public override string Key => "crm.update_contact";

    public override string ModuleKey => CrmModule.ModuleKey;

    public override string Title => "修改联系人";

    public override string Description => "修改联系人信息，只需要传要改的字段。";

    public override string RequiredFeature => CrmFeatures.Manage;

    protected override async Task<CrmContactDto> ExecuteAsync(UpdateCrmContactInput input, ActionContext context, CancellationToken cancellationToken)
    {
        var current = (await dispatcher.SendAsync(new GetCrmContactQuery(input.ContactId), cancellationToken).ConfigureAwait(false)).Contact;
        return await dispatcher.SendAsync(new UpdateCrmContactCommand(current.Id, new SaveCrmContactRequest
        {
            Name = input.Name ?? current.Name,
            CompanyId = input.CompanyId ?? current.CompanyId,
            Title = input.Title ?? current.Title,
            Email = input.Email ?? current.Email,
            Phone = input.Phone ?? current.Phone,
            Description = input.Description ?? current.Description,
            Tags = input.Tags ?? current.Tags,
            OwnerUserId = input.OwnerUserId ?? current.Owner?.Id,
            ExpectedUpdatedAt = current.UpdatedAt,
        }), cancellationToken).ConfigureAwait(false);
    }
}

public sealed class CreateCrmDealInput
{
    [Description("商机名称，例如「星火科技 2027 年度软件采购」")]
    public required string Title { get; init; }

    [Description("客户 ID")]
    public Guid? CompanyId { get; init; }

    [Description("主要联系人 ID；只填联系人时自动带上其所属客户")]
    public Guid? ContactId { get; init; }

    [Description("所在阶段名称；省略时放进第一个进行中的阶段")]
    public string? StageName { get; init; }

    [Description("预计金额")]
    public decimal? Amount { get; init; }

    [Description("货币代码，默认 CNY")]
    public string Currency { get; init; } = "CNY";

    [Description("预计成交日期，格式 yyyy-MM-dd")]
    public DateOnly? ExpectedCloseDate { get; init; }

    [Description("需求、竞争情况、关键决策人等")]
    public string Description { get; init; } = string.Empty;

    [Description("负责的成员（用户 ID）；留空时由创建者负责，智能体创建时为空")]
    public Guid? OwnerUserId { get; init; }
}

internal sealed class CreateCrmDealAction(IRequestDispatcher dispatcher) : PlatformAction<CreateCrmDealInput, CrmDealDto>
{
    public override string Key => "crm.create_deal";

    public override string ModuleKey => CrmModule.ModuleKey;

    public override string Title => "新建商机";

    public override string Description => "新建商机（一笔可能成交的生意）。先用 crm.search 找到客户和联系人的 ID。";

    public override string RequiredFeature => CrmFeatures.Manage;

    protected override async Task<CrmDealDto> ExecuteAsync(CreateCrmDealInput input, ActionContext context, CancellationToken cancellationToken)
    {
        Guid? stageId = string.IsNullOrWhiteSpace(input.StageName)
            ? null
            : (await CrmStageNames.ResolveAsync(dispatcher, input.StageName, "stageName", cancellationToken).ConfigureAwait(false)).Id;
        return await dispatcher.SendAsync(new CreateCrmDealCommand(new SaveCrmDealRequest
        {
            Title = input.Title,
            CompanyId = input.CompanyId,
            ContactId = input.ContactId,
            StageId = stageId,
            Amount = input.Amount,
            Currency = input.Currency,
            ExpectedCloseDate = input.ExpectedCloseDate,
            Description = input.Description,
            OwnerUserId = input.OwnerUserId,
        }), cancellationToken).ConfigureAwait(false);
    }
}

public sealed class UpdateCrmDealInput
{
    [Description("商机 ID")]
    public required Guid DealId { get; init; }

    [Description("新名称；不改就省略")]
    public string? Title { get; init; }

    [Description("改关联的客户；不改就省略")]
    public Guid? CompanyId { get; init; }

    [Description("改主要联系人；不改就省略")]
    public Guid? ContactId { get; init; }

    [Description("预计金额；不改就省略")]
    public decimal? Amount { get; init; }

    [Description("货币代码；不改就省略")]
    public string? Currency { get; init; }

    [Description("预计成交日期 yyyy-MM-dd；不改就省略")]
    public DateOnly? ExpectedCloseDate { get; init; }

    [Description("描述（整体替换）；不改就省略")]
    public string? Description { get; init; }

    [Description("改由哪个成员负责（用户 ID）；不改就省略")]
    public Guid? OwnerUserId { get; init; }
}

internal sealed class UpdateCrmDealAction(IRequestDispatcher dispatcher) : PlatformAction<UpdateCrmDealInput, CrmDealDto>
{
    public override string Key => "crm.update_deal";

    public override string ModuleKey => CrmModule.ModuleKey;

    public override string Title => "修改商机";

    public override string Description => "修改商机的金额、预计成交日期、描述等，只需要传要改的字段。推进阶段（包括赢单、输单）用 crm.move_deal。";

    public override string RequiredFeature => CrmFeatures.Manage;

    protected override async Task<CrmDealDto> ExecuteAsync(UpdateCrmDealInput input, ActionContext context, CancellationToken cancellationToken)
    {
        var current = (await dispatcher.SendAsync(new GetCrmDealQuery(input.DealId), cancellationToken).ConfigureAwait(false)).Deal;
        return await dispatcher.SendAsync(new UpdateCrmDealCommand(current.Id, new SaveCrmDealRequest
        {
            Title = input.Title ?? current.Title,
            CompanyId = input.CompanyId ?? current.CompanyId,
            ContactId = input.ContactId ?? current.ContactId,
            Amount = input.Amount ?? current.Amount,
            Currency = input.Currency ?? current.Currency,
            ExpectedCloseDate = input.ExpectedCloseDate ?? current.ExpectedCloseDate,
            Description = input.Description ?? current.Description,
            OwnerUserId = input.OwnerUserId ?? current.Owner?.Id,
            ExpectedUpdatedAt = current.UpdatedAt,
        }), cancellationToken).ConfigureAwait(false);
    }
}

public sealed class MoveCrmDealInput
{
    [Description("商机 ID")]
    public required Guid DealId { get; init; }

    [Description("目标阶段名称，例如「商务谈判」「赢单」「输单」")]
    public required string StageName { get; init; }

    [Description("推进说明，例如赢单或输单的原因，会记入跟进记录")]
    public string? Note { get; init; }
}

internal sealed class MoveCrmDealAction(IRequestDispatcher dispatcher) : PlatformAction<MoveCrmDealInput, CrmDealDto>
{
    public override string Key => "crm.move_deal";

    public override string ModuleKey => CrmModule.ModuleKey;

    public override string Title => "推进商机";

    public override string Description => "把商机移到另一个销售阶段（包括赢单、输单），自动记录阶段变化。赢单或输单时请在 note 里写明原因。";

    public override string RequiredFeature => CrmFeatures.Manage;

    protected override async Task<CrmDealDto> ExecuteAsync(MoveCrmDealInput input, ActionContext context, CancellationToken cancellationToken)
    {
        var stage = await CrmStageNames.ResolveAsync(dispatcher, input.StageName, "stageName", cancellationToken).ConfigureAwait(false);
        return await dispatcher.SendAsync(new MoveCrmDealCommand(input.DealId, stage.Id, input.Note, context.RunId), cancellationToken).ConfigureAwait(false);
    }
}

public sealed class LogCrmActivityInput
{
    [Description("类型：note 备注、call 电话、meeting 会议、email 邮件")]
    public required CrmActivityKind Kind { get; init; }

    [Description("内容：沟通了什么、对方反馈、下一步计划")]
    public required string Content { get; init; }

    [Description("实际发生时间（ISO 8601）；省略为现在")]
    public DateTimeOffset? OccurredAt { get; init; }

    [Description("关联的客户 ID")]
    public Guid? CompanyId { get; init; }

    [Description("关联的联系人 ID")]
    public Guid? ContactId { get; init; }

    [Description("关联的商机 ID")]
    public Guid? DealId { get; init; }
}

internal sealed class LogCrmActivityAction(IRequestDispatcher dispatcher) : PlatformAction<LogCrmActivityInput, CrmActivityDto>
{
    public override string Key => "crm.log_activity";

    public override string ModuleKey => CrmModule.ModuleKey;

    public override string Title => "记录跟进";

    public override string Description => "记录一次与客户的沟通（电话、会议、邮件或备注），至少关联客户、联系人、商机中的一个。整理会议纪要、通话记录后用它归档到对应客户。";

    public override string RequiredFeature => CrmFeatures.Manage;

    protected override Task<CrmActivityDto> ExecuteAsync(LogCrmActivityInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new LogCrmActivityCommand(new LogCrmActivityRequest
        {
            Kind = input.Kind,
            Content = input.Content,
            OccurredAt = input.OccurredAt,
            CompanyId = input.CompanyId,
            ContactId = input.ContactId,
            DealId = input.DealId,
        }, context.RunId), cancellationToken);
}

/// <summary>
/// 让模型按名称指定阶段（不必先查 ID）。找不到时把可选阶段列在错误里，模型据此改正。
/// </summary>
internal static class CrmStageNames
{
    public static async Task<CrmStageDto> ResolveAsync(IRequestDispatcher dispatcher, string name, string field, CancellationToken cancellationToken)
    {
        var stages = await dispatcher.SendAsync(new ListCrmStagesQuery(), cancellationToken).ConfigureAwait(false);
        var wanted = name.Trim();
        return stages.FirstOrDefault(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase))
               ?? throw new ValidationFailedException(new Dictionary<string, string[]>
               {
                   [field] = [$"没有名为「{wanted}」的阶段，可选：{string.Join("、", stages.Select(s => s.Name))}。"],
               });
    }
}

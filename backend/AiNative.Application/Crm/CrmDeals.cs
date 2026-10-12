using System.Text.RegularExpressions;
using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Agents;
using AiNative.Application.Common;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Common;
using AiNative.Contracts.Crm;
using AiNative.Domain.Crm;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Crm;

[RequireFeature(CrmFeatures.View)]
public sealed record ListCrmDealsQuery(
    string? Search = null,
    Guid? StageId = null,
    CrmDealStatus? Status = null,
    Guid? CompanyId = null,
    Guid? ContactId = null,
    Guid? OwnerUserId = null,
    int Page = 1,
    int PageSize = 50) : IQuery<PagedResult<CrmDealDto>>;

[RequireFeature(CrmFeatures.View)]
public sealed record GetCrmDealQuery(Guid Id) : IQuery<CrmDealDetailDto>;

[RequireFeature(CrmFeatures.View)]
public sealed record GetCrmPipelineQuery(Guid? OwnerUserId = null) : IQuery<CrmPipelineDto>;

[RequireFeature(CrmFeatures.Manage)]
public sealed record CreateCrmDealCommand(SaveCrmDealRequest Request) : ICommand<CrmDealDto>;

[RequireFeature(CrmFeatures.Manage)]
public sealed record UpdateCrmDealCommand(Guid Id, SaveCrmDealRequest Request) : ICommand<CrmDealDto>;

[RequireFeature(CrmFeatures.Manage)]
public sealed record MoveCrmDealCommand(Guid Id, Guid StageId, string? Note = null, Guid? RunId = null) : ICommand<CrmDealDto>;

[RequireFeature(CrmFeatures.Manage)]
public sealed record DeleteCrmDealCommand(Guid Id) : ICommand<Unit>;

public sealed partial class SaveCrmDealRequestValidator : AbstractValidator<SaveCrmDealRequest>
{
    public SaveCrmDealRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().WithMessage("请填写商机名称。").Text(200, "商机名称").OverridePropertyName("title");
        RuleFor(x => x.Currency).Must(c => CurrencyPattern().IsMatch(c.Trim().ToUpperInvariant()))
            .WithMessage("货币请填写 3 位字母代码，例如 CNY、USD。").OverridePropertyName("currency");
        RuleFor(x => x.Amount).GreaterThanOrEqualTo(0).LessThan(10_000_000_000_000_000m).When(x => x.Amount.HasValue)
            .WithMessage("金额必须是不小于 0 的数字。").OverridePropertyName("amount");
        RuleFor(x => x.Description).Text(10_000, "描述").OverridePropertyName("description");
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();
}

public sealed class CreateCrmDealCommandValidator : AbstractValidator<CreateCrmDealCommand>
{
    public CreateCrmDealCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveCrmDealRequestValidator());
}

public sealed class UpdateCrmDealCommandValidator : AbstractValidator<UpdateCrmDealCommand>
{
    public UpdateCrmDealCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveCrmDealRequestValidator());
}

public sealed class MoveCrmDealCommandValidator : AbstractValidator<MoveCrmDealCommand>
{
    public MoveCrmDealCommandValidator() => RuleFor(x => x.Note).MaximumLength(2_000).WithMessage("推进说明最多 2000 个字。").OverridePropertyName("note");
}

internal sealed class CrmDealHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    CrmRecords records,
    CrmCustomFields customFields,
    ITextMatcher text,
    IChangeFeed changes,
    TimeProvider clock) :
    IRequestHandler<ListCrmDealsQuery, PagedResult<CrmDealDto>>,
    IRequestHandler<GetCrmDealQuery, CrmDealDetailDto>,
    IRequestHandler<GetCrmPipelineQuery, CrmPipelineDto>,
    IRequestHandler<CreateCrmDealCommand, CrmDealDto>,
    IRequestHandler<UpdateCrmDealCommand, CrmDealDto>,
    IRequestHandler<MoveCrmDealCommand, CrmDealDto>,
    IRequestHandler<DeleteCrmDealCommand, Unit>
{
    /// <summary>
    /// 看板上赢单、输单两列只展示最近这么多天结束的商机。
    /// </summary>
    public const int ClosedWindowDays = 30;

    public async Task<PagedResult<CrmDealDto>> HandleAsync(ListCrmDealsQuery request, CancellationToken cancellationToken)
    {
        var (page, pageSize) = CrmRecords.Paging(request.Page, request.PageSize);
        var query = db.CrmDeals.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            query = text.WhereContains(query, request.Search, d => d.Title);
        }

        if (request.StageId is { } stageId)
        {
            query = query.Where(d => d.StageId == stageId);
        }

        if (request.Status is { } status)
        {
            query = query.Where(d => d.Status == status);
        }

        if (request.CompanyId is { } companyId)
        {
            query = query.Where(d => d.CompanyId == companyId);
        }

        if (request.ContactId is { } contactId)
        {
            query = query.Where(d => d.ContactId == contactId);
        }

        if (request.OwnerUserId is { } owner)
        {
            query = query.Where(d => d.OwnerUserId == owner);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query.OrderByDescending(d => d.UpdatedAt).ThenByDescending(d => d.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<CrmDealDto>(await CrmMapping.DealsAsync(db, items, cancellationToken).ConfigureAwait(false), total, page, pageSize);
    }

    public async Task<CrmDealDetailDto> HandleAsync(GetCrmDealQuery request, CancellationToken cancellationToken)
    {
        var deal = await db.CrmDeals.AsNoTracking().FirstOrDefaultAsync(d => d.Id == request.Id, cancellationToken).ConfigureAwait(false)
                   ?? throw new NotFoundException("crm_deal", request.Id);
        var activities = await db.CrmActivities.AsNoTracking().Where(a => a.DealId == deal.Id)
            .OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id)
            .Take(CrmRecords.TimelinePageSize).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new CrmDealDetailDto(
            (await CrmMapping.DealsAsync(db, [deal], cancellationToken).ConfigureAwait(false))[0],
            await CrmMapping.ActivitiesAsync(db, activities, cancellationToken).ConfigureAwait(false));
    }

    public async Task<CrmPipelineDto> HandleAsync(GetCrmPipelineQuery request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var windowStart = now.AddDays(-ClosedWindowDays);
        var monthStart = BillingPeriod.MonthStart(now);
        var stages = await db.CrmStages.AsNoTracking().OrderBy(s => s.Position).ToListAsync(cancellationToken).ConfigureAwait(false);
        var deals = db.CrmDeals.AsNoTracking();
        if (request.OwnerUserId is { } owner)
        {
            deals = deals.Where(d => d.OwnerUserId == owner);
        }

        var open = deals.Where(d => d.Status == CrmDealStatus.Open);
        var closed = deals.Where(d => d.Status != CrmDealStatus.Open && d.ClosedAt >= windowStart);
        var aggregates = await deals.Where(d => d.Status == CrmDealStatus.Open || d.ClosedAt >= windowStart)
            .GroupBy(d => new { d.StageId, d.Currency })
            .Select(g => new { g.Key.StageId, g.Key.Currency, Count = g.Count(), Amount = g.Sum(d => d.Amount ?? 0m) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var wonThisMonth = await deals.Where(d => d.Status == CrmDealStatus.Won && d.ClosedAt >= monthStart)
            .GroupBy(d => d.Currency)
            .Select(g => new CrmAmountDto(g.Key, g.Sum(d => d.Amount ?? 0m)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // 看板卡片：进行中的最多 1000 个、最近结束的最多 300 个，列头的数量和金额来自上面的聚合，不受截断影响。
        var cards = await open.OrderByDescending(d => d.UpdatedAt).Take(1000).ToListAsync(cancellationToken).ConfigureAwait(false);
        cards.AddRange(await closed.OrderByDescending(d => d.ClosedAt).Take(300).ToListAsync(cancellationToken).ConfigureAwait(false));
        var dtos = await CrmMapping.DealsAsync(db, cards, cancellationToken).ConfigureAwait(false);

        var columns = stages.Select(stage =>
        {
            var rows = aggregates.Where(a => a.StageId == stage.Id).ToList();
            return new CrmPipelineStageDto(
                stage.ToDto(),
                rows.Sum(r => r.Count),
                Totals(rows.Select(r => (r.Currency, r.Amount))),
                [.. dtos.Where(d => d.StageId == stage.Id)]);
        }).ToList();
        var openStages = stages.Where(s => s.Kind == CrmStageKind.Open).ToDictionary(s => s.Id);
        var openRows = aggregates.Where(a => openStages.ContainsKey(a.StageId)).ToList();
        return new CrmPipelineDto(
            columns,
            Totals(openRows.Select(r => (r.Currency, r.Amount))),
            Totals(openRows.Select(r => (r.Currency, r.Amount * openStages[r.StageId].Probability / 100m))),
            [.. wonThisMonth.OrderBy(t => t.Currency, StringComparer.Ordinal)],
            ClosedWindowDays);
    }

    public async Task<CrmDealDto> HandleAsync(CreateCrmDealCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var owner = records.DefaultOwner(r.OwnerUserId);
        await records.EnsureOwnerAsync(owner, cancellationToken).ConfigureAwait(false);
        var stage = r.StageId is { } stageId
            ? await StageAsync(stageId, cancellationToken).ConfigureAwait(false)
            : await db.CrmStages.AsNoTracking().Where(s => s.Kind == CrmStageKind.Open).OrderBy(s => s.Position)
                  .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
              ?? throw new ConflictException("crm.no_stage", "还没有配置进行中的销售阶段，请先在看板的阶段设置里添加。");
        var deal = new CrmDeal
        {
            Title = r.Title.Trim(),
            CreatedBy = tenant.Actor,
            OwnerUserId = owner,
            CustomFields = await customFields.ForCreateAsync(CrmRecordType.Deal, r.CustomFields, cancellationToken).ConfigureAwait(false),
        };
        await ApplyAsync(deal, r, cancellationToken).ConfigureAwait(false);
        deal.MoveTo(stage, clock.GetUtcNow());
        db.CrmDeals.Add(deal);
        PublishChanges(deal, RealtimeChangeKind.Created);
        var dto = (await CrmMapping.DealsAsync(db, [deal], cancellationToken).ConfigureAwait(false))[0];
        await records.PublishAsync(CrmEventKeys.DealCreated, deal.Id, () => Task.FromResult(new Dictionary<string, object?> { ["deal"] = dto }), cancellationToken)
            .ConfigureAwait(false);
        return dto;
    }

    public async Task<CrmDealDto> HandleAsync(UpdateCrmDealCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var deal = await records.DealAsync(request.Id, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (r.ExpectedUpdatedAt is { } expected)
        {
            await OptimisticConcurrency.EnsureUnchangedAsync(
                db.CrmDeals, deal.Id, expected, now, "crm_deal.modified", "商机刚被其他人修改过，已刷新为最新内容，请确认后再保存。", cancellationToken)
                .ConfigureAwait(false);
        }

        await records.EnsureOwnerAsync(r.OwnerUserId, cancellationToken).ConfigureAwait(false);
        var values = await customFields.ForUpdateAsync(CrmRecordType.Deal, r.CustomFields, cancellationToken).ConfigureAwait(false);
        var previousCompany = deal.CompanyId;
        var previousContact = deal.ContactId;
        deal.Title = r.Title.Trim();
        deal.OwnerUserId = r.OwnerUserId;
        deal.CustomFields = values ?? deal.CustomFields;
        await ApplyAsync(deal, r, cancellationToken).ConfigureAwait(false);
        deal.UpdatedAt = now;
        PublishChanges(deal, RealtimeChangeKind.Updated, previousCompany, previousContact);
        if (r.StageId is { } stageId && stageId != deal.StageId)
        {
            return await MoveAsync(deal, await StageAsync(stageId, cancellationToken).ConfigureAwait(false), null, null, cancellationToken).ConfigureAwait(false);
        }

        return (await CrmMapping.DealsAsync(db, [deal], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<CrmDealDto> HandleAsync(MoveCrmDealCommand request, CancellationToken cancellationToken)
    {
        var deal = await records.DealAsync(request.Id, cancellationToken).ConfigureAwait(false);
        var stage = await StageAsync(request.StageId, cancellationToken).ConfigureAwait(false);
        if (stage.Id == deal.StageId)
        {
            return (await CrmMapping.DealsAsync(db, [deal], cancellationToken).ConfigureAwait(false))[0];
        }

        return await MoveAsync(deal, stage, request.Note, request.RunId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 删除商机：它的阶段变化记录和只属于它的跟进记录一并删除，同时记在客户或联系人上的保留。
    /// </summary>
    public async Task<Unit> HandleAsync(DeleteCrmDealCommand request, CancellationToken cancellationToken)
    {
        var deal = await records.DealAsync(request.Id, cancellationToken).ConfigureAwait(false);
        await db.CrmActivities
            .Where(a => a.DealId == deal.Id && (a.Kind == CrmActivityKind.StageChange || (a.CompanyId == null && a.ContactId == null)))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.CrmDeals.Remove(deal);
        PublishChanges(deal, RealtimeChangeKind.Deleted);
        return Unit.Value;
    }

    /// <summary>
    /// 推进阶段：以"阶段仍是读到的那个"为条件先锁住商机，并发的另一次推进会冲突而不是悄悄覆盖；
    /// 自动记一条阶段变化，并发布事件（赢单、输单也走这里）。
    /// </summary>
    private async Task<CrmDealDto> MoveAsync(CrmDeal deal, CrmStage stage, string? note, Guid? runId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var previousStageId = deal.StageId;
        var locked = await db.CrmDeals.Where(d => d.Id == deal.Id && d.StageId == previousStageId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.UpdatedAt, now), cancellationToken).ConfigureAwait(false);
        if (locked == 0)
        {
            throw new ConflictException("crm_deal.moved", "商机刚被其他人推进过，已刷新为最新状态，请确认后再操作。");
        }

        var from = await db.CrmStages.AsNoTracking().FirstAsync(s => s.Id == previousStageId, cancellationToken).ConfigureAwait(false);
        deal.MoveTo(stage, now);
        deal.UpdatedAt = now;
        deal.LastActivityAt = now;
        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        var activity = new CrmActivity
        {
            Kind = CrmActivityKind.StageChange,
            Content = trimmedNote is null ? $"「{from.Name}」→「{stage.Name}」" : $"「{from.Name}」→「{stage.Name}」：{trimmedNote}",
            OccurredAt = now,
            CompanyId = deal.CompanyId,
            ContactId = deal.ContactId,
            DealId = deal.Id,
            Author = tenant.Actor,
            RunId = runId,
        };
        db.CrmActivities.Add(activity);
        await records.TouchAsync(deal.CompanyId, deal.ContactId, null, now, cancellationToken).ConfigureAwait(false);
        PublishChanges(deal, RealtimeChangeKind.Updated);
        changes.Publish(CrmRealtime.Activity, activity.Id, RealtimeChangeKind.Created);

        var dto = (await CrmMapping.DealsAsync(db, [deal], cancellationToken).ConfigureAwait(false))[0];
        await records.PublishAsync(CrmEventKeys.DealStageChanged, deal.Id, () => Task.FromResult(new Dictionary<string, object?>
        {
            ["deal"] = dto,
            ["from"] = from.ToDto(),
            ["to"] = stage.ToDto(),
            ["note"] = trimmedNote,
        }), cancellationToken).ConfigureAwait(false);
        return dto;
    }

    /// <summary>
    /// 写入关联与金额。只选了联系人时自动带上联系人所属的客户；联系人与客户对不上时拒绝。
    /// </summary>
    private async Task ApplyAsync(CrmDeal deal, SaveCrmDealRequest r, CancellationToken cancellationToken)
    {
        var companyId = r.CompanyId;
        if (r.ContactId is { } contactId)
        {
            var contactCompany = await db.CrmContacts.Where(c => c.Id == contactId).Select(c => new { c.CompanyId })
                                     .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                                 ?? throw new NotFoundException("crm_contact", contactId);
            if (companyId is not null && contactCompany.CompanyId is not null && contactCompany.CompanyId != companyId)
            {
                throw new ValidationFailedException(new Dictionary<string, string[]> { ["contactId"] = ["联系人不属于所选客户。"] });
            }

            companyId ??= contactCompany.CompanyId;
        }

        await records.EnsureCompanyExistsAsync(companyId, cancellationToken).ConfigureAwait(false);
        deal.CompanyId = companyId;
        deal.ContactId = r.ContactId;
        deal.Amount = r.Amount;
        deal.Currency = r.Currency.Trim().ToUpperInvariant();
        deal.ExpectedCloseDate = r.ExpectedCloseDate;
        deal.Description = r.Description.Trim();
    }

    private async Task<CrmStage> StageAsync(Guid id, CancellationToken cancellationToken)
        => await db.CrmStages.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken).ConfigureAwait(false)
           ?? throw new NotFoundException("crm_stage", id);

    private void PublishChanges(CrmDeal deal, string kind, Guid? previousCompany = null, Guid? previousContact = null)
    {
        changes.Publish(CrmRealtime.Deal, deal.Id, kind);
        foreach (var companyId in new[] { previousCompany, deal.CompanyId }.OfType<Guid>().Distinct())
        {
            changes.Publish(CrmRealtime.Company, companyId, RealtimeChangeKind.Updated);
        }

        foreach (var contactId in new[] { previousContact, deal.ContactId }.OfType<Guid>().Distinct())
        {
            changes.Publish(CrmRealtime.Contact, contactId, RealtimeChangeKind.Updated);
        }
    }

    private static IReadOnlyList<CrmAmountDto> Totals(IEnumerable<(string Currency, decimal Amount)> rows)
        => [.. rows.GroupBy(r => r.Currency).Select(g => new CrmAmountDto(g.Key, g.Sum(r => r.Amount))).OrderBy(t => t.Currency, StringComparer.Ordinal)];
}

using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Common;
using AiNative.Contracts.Crm;
using AiNative.Domain.Crm;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Crm;

/// <summary>
/// 时间线：按客户、联系人或商机过滤；都不填时是整个空间的最近动态。
/// </summary>
[RequireFeature(CrmFeatures.View)]
public sealed record ListCrmActivitiesQuery(Guid? CompanyId = null, Guid? ContactId = null, Guid? DealId = null, int Page = 1, int PageSize = 50)
    : IQuery<PagedResult<CrmActivityDto>>;

[RequireFeature(CrmFeatures.Manage)]
public sealed record LogCrmActivityCommand(LogCrmActivityRequest Request, Guid? RunId = null) : ICommand<CrmActivityDto>;

[RequireFeature(CrmFeatures.Manage)]
public sealed record DeleteCrmActivityCommand(Guid Id) : ICommand<Unit>;

public sealed class LogCrmActivityCommandValidator : AbstractValidator<LogCrmActivityCommand>
{
    public LogCrmActivityCommandValidator()
    {
        RuleFor(x => x.Request.Kind).NotEqual(CrmActivityKind.StageChange).WithMessage("阶段变化由系统在推进商机时自动记录。").OverridePropertyName("kind");
        RuleFor(x => x.Request.Content).NotEmpty().WithMessage("请填写跟进内容。").MaximumLength(10_000).WithMessage("跟进内容最多 10000 个字。")
            .OverridePropertyName("content");
        RuleFor(x => x.Request).Must(r => r.CompanyId.HasValue || r.ContactId.HasValue || r.DealId.HasValue)
            .WithMessage("跟进记录需要关联客户、联系人或商机中的至少一个。").OverridePropertyName("companyId");
    }
}

internal sealed class CrmActivityHandlers(IAppDbContext db, ITenantContext tenant, CrmRecords records, IChangeFeed changes, TimeProvider clock) :
    IRequestHandler<ListCrmActivitiesQuery, PagedResult<CrmActivityDto>>,
    IRequestHandler<LogCrmActivityCommand, CrmActivityDto>,
    IRequestHandler<DeleteCrmActivityCommand, Unit>
{
    public async Task<PagedResult<CrmActivityDto>> HandleAsync(ListCrmActivitiesQuery request, CancellationToken cancellationToken)
    {
        var (page, pageSize) = CrmRecords.Paging(request.Page, request.PageSize);
        var query = db.CrmActivities.AsNoTracking();
        if (request.CompanyId is { } companyId)
        {
            query = query.Where(a => a.CompanyId == companyId);
        }

        if (request.ContactId is { } contactId)
        {
            query = query.Where(a => a.ContactId == contactId);
        }

        if (request.DealId is { } dealId)
        {
            query = query.Where(a => a.DealId == dealId);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query.OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<CrmActivityDto>(await CrmMapping.ActivitiesAsync(db, items, cancellationToken).ConfigureAwait(false), total, page, pageSize);
    }

    /// <summary>
    /// 记录跟进。记在商机或联系人上时自动带上所属客户，客户的时间线因此能看到全部往来；
    /// 同时指定的客户、联系人、商机必须属于同一个客户，否则拒绝。
    /// </summary>
    public async Task<CrmActivityDto> HandleAsync(LogCrmActivityCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var deal = r.DealId is { } dealId ? await records.DealAsync(dealId, cancellationToken).ConfigureAwait(false) : null;
        var contact = r.ContactId is { } contactId ? await records.ContactAsync(contactId, cancellationToken).ConfigureAwait(false) : null;
        var errors = new Dictionary<string, string[]>();
        if (r.CompanyId is { } requested && deal?.CompanyId is { } dealCompany && dealCompany != requested)
        {
            errors["dealId"] = ["商机不属于所选客户。"];
        }

        if (contact?.CompanyId is { } contactCompany
            && ((r.CompanyId is { } chosen && contactCompany != chosen) || (deal?.CompanyId is { } ofDeal && contactCompany != ofDeal)))
        {
            errors["contactId"] = [r.CompanyId is null ? "联系人与商机不属于同一个客户。" : "联系人不属于所选客户。"];
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        var companyId = r.CompanyId ?? deal?.CompanyId ?? contact?.CompanyId;
        await records.EnsureCompanyExistsAsync(companyId, cancellationToken).ConfigureAwait(false);
        var activity = new CrmActivity
        {
            Kind = r.Kind,
            Content = r.Content.Trim(),
            OccurredAt = r.OccurredAt ?? clock.GetUtcNow(),
            CompanyId = companyId,
            ContactId = r.ContactId,
            DealId = r.DealId,
            Author = tenant.Actor,
            RunId = request.RunId,
        };
        db.CrmActivities.Add(activity);
        await records.TouchAsync(activity.CompanyId, activity.ContactId, activity.DealId, activity.OccurredAt, cancellationToken).ConfigureAwait(false);
        PublishChanges(activity, RealtimeChangeKind.Created);
        var dto = (await CrmMapping.ActivitiesAsync(db, [activity], cancellationToken).ConfigureAwait(false))[0];
        await records.PublishAsync(CrmEventKeys.ActivityLogged, activity.Id, () => Task.FromResult(new Dictionary<string, object?> { ["activity"] = dto }), cancellationToken)
            .ConfigureAwait(false);
        return dto;
    }

    public async Task<Unit> HandleAsync(DeleteCrmActivityCommand request, CancellationToken cancellationToken)
    {
        var activity = await db.CrmActivities.FirstOrDefaultAsync(a => a.Id == request.Id, cancellationToken).ConfigureAwait(false)
                       ?? throw new NotFoundException("crm_activity", request.Id);
        if (activity.Kind == CrmActivityKind.StageChange)
        {
            throw new ConflictException("crm_activity.system", "阶段变化记录由系统生成，不能删除。");
        }

        db.CrmActivities.Remove(activity);
        await records.RecomputeLastActivityAsync(activity.CompanyId, activity.ContactId, activity.DealId, activity.Id, cancellationToken).ConfigureAwait(false);
        PublishChanges(activity, RealtimeChangeKind.Deleted);
        return Unit.Value;
    }

    private void PublishChanges(CrmActivity activity, string kind)
    {
        changes.Publish(CrmRealtime.Activity, activity.Id, kind);
        if (activity.CompanyId is { } companyId)
        {
            changes.Publish(CrmRealtime.Company, companyId, RealtimeChangeKind.Updated);
        }

        if (activity.ContactId is { } contactId)
        {
            changes.Publish(CrmRealtime.Contact, contactId, RealtimeChangeKind.Updated);
        }

        if (activity.DealId is { } dealId)
        {
            changes.Publish(CrmRealtime.Deal, dealId, RealtimeChangeKind.Updated);
        }
    }
}

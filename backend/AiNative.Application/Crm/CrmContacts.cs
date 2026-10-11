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

[RequireFeature(CrmFeatures.View)]
public sealed record ListCrmContactsQuery(string? Search = null, Guid? CompanyId = null, Guid? OwnerUserId = null, string? Tag = null, int Page = 1, int PageSize = 50)
    : IQuery<PagedResult<CrmContactDto>>;

[RequireFeature(CrmFeatures.View)]
public sealed record GetCrmContactQuery(Guid Id) : IQuery<CrmContactDetailDto>;

[RequireFeature(CrmFeatures.Manage)]
public sealed record CreateCrmContactCommand(SaveCrmContactRequest Request) : ICommand<CrmContactDto>;

[RequireFeature(CrmFeatures.Manage)]
public sealed record UpdateCrmContactCommand(Guid Id, SaveCrmContactRequest Request) : ICommand<CrmContactDto>;

[RequireFeature(CrmFeatures.Manage)]
public sealed record DeleteCrmContactCommand(Guid Id) : ICommand<Unit>;

public sealed class SaveCrmContactRequestValidator : AbstractValidator<SaveCrmContactRequest>
{
    public SaveCrmContactRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("请填写联系人姓名。").Text(100, "姓名").OverridePropertyName("name");
        RuleFor(x => x.Title).Text(100, "职位").OverridePropertyName("title");
        RuleFor(x => x.Email).Text(254, "邮箱").OverridePropertyName("email");
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email)).WithMessage("邮箱格式不正确。").OverridePropertyName("email");
        RuleFor(x => x.Phone).Text(50, "电话").OverridePropertyName("phone");
        RuleFor(x => x.Description).Text(10_000, "备注").OverridePropertyName("description");
        this.Tags(x => x.Tags);
    }
}

public sealed class CreateCrmContactCommandValidator : AbstractValidator<CreateCrmContactCommand>
{
    public CreateCrmContactCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveCrmContactRequestValidator());
}

public sealed class UpdateCrmContactCommandValidator : AbstractValidator<UpdateCrmContactCommand>
{
    public UpdateCrmContactCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveCrmContactRequestValidator());
}

internal sealed class CrmContactHandlers(IAppDbContext db, ITenantContext tenant, CrmRecords records, ITextMatcher text, IChangeFeed changes, TimeProvider clock) :
    IRequestHandler<ListCrmContactsQuery, PagedResult<CrmContactDto>>,
    IRequestHandler<GetCrmContactQuery, CrmContactDetailDto>,
    IRequestHandler<CreateCrmContactCommand, CrmContactDto>,
    IRequestHandler<UpdateCrmContactCommand, CrmContactDto>,
    IRequestHandler<DeleteCrmContactCommand, Unit>
{
    public async Task<PagedResult<CrmContactDto>> HandleAsync(ListCrmContactsQuery request, CancellationToken cancellationToken)
    {
        var (page, pageSize) = CrmRecords.Paging(request.Page, request.PageSize);
        var query = db.CrmContacts.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            query = text.WhereContains(query, request.Search, c => c.Name, c => c.Email, c => c.Phone, c => c.Title);
        }

        if (request.CompanyId is { } companyId)
        {
            query = query.Where(c => c.CompanyId == companyId);
        }

        if (request.OwnerUserId is { } owner)
        {
            query = query.Where(c => c.OwnerUserId == owner);
        }

        if (!string.IsNullOrWhiteSpace(request.Tag))
        {
            var tag = request.Tag.Trim();
            query = query.Where(c => c.Tags.Contains(tag));
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query.OrderByDescending(c => c.LastActivityAt ?? c.CreatedAt).ThenByDescending(c => c.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<CrmContactDto>(await CrmMapping.ContactsAsync(db, items, cancellationToken).ConfigureAwait(false), total, page, pageSize);
    }

    public async Task<CrmContactDetailDto> HandleAsync(GetCrmContactQuery request, CancellationToken cancellationToken)
    {
        var contact = await db.CrmContacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.Id, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("crm_contact", request.Id);
        var related = db.CrmDeals.AsNoTracking().Where(d => d.ContactId == contact.Id);
        var deals = await related.OrderBy(d => d.Status).ThenByDescending(d => d.UpdatedAt)
            .Take(CrmRecords.RelatedLimit).ToListAsync(cancellationToken).ConfigureAwait(false);
        var dealTotal = deals.Count < CrmRecords.RelatedLimit ? deals.Count : await related.CountAsync(cancellationToken).ConfigureAwait(false);
        var activities = await db.CrmActivities.AsNoTracking().Where(a => a.ContactId == contact.Id)
            .OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id)
            .Take(CrmRecords.TimelinePageSize).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new CrmContactDetailDto(
            (await CrmMapping.ContactsAsync(db, [contact], cancellationToken).ConfigureAwait(false))[0],
            await CrmMapping.DealsAsync(db, deals, cancellationToken).ConfigureAwait(false),
            await CrmMapping.ActivitiesAsync(db, activities, cancellationToken).ConfigureAwait(false),
            dealTotal);
    }

    public async Task<CrmContactDto> HandleAsync(CreateCrmContactCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var owner = records.DefaultOwner(r.OwnerUserId);
        await records.EnsureOwnerAsync(owner, cancellationToken).ConfigureAwait(false);
        await records.EnsureCompanyExistsAsync(r.CompanyId, cancellationToken).ConfigureAwait(false);
        var contact = new CrmContact { Name = r.Name.Trim(), CreatedBy = tenant.Actor, OwnerUserId = owner };
        Apply(contact, r);
        db.CrmContacts.Add(contact);
        changes.Publish(CrmRealtime.Contact, contact.Id, RealtimeChangeKind.Created);
        if (contact.CompanyId is { } companyId)
        {
            changes.Publish(CrmRealtime.Company, companyId, RealtimeChangeKind.Updated);
        }

        var dto = (await CrmMapping.ContactsAsync(db, [contact], cancellationToken).ConfigureAwait(false))[0];
        await records.PublishAsync(CrmEventKeys.ContactCreated, contact.Id, () => Task.FromResult(new Dictionary<string, object?> { ["contact"] = dto }), cancellationToken)
            .ConfigureAwait(false);
        return dto;
    }

    public async Task<CrmContactDto> HandleAsync(UpdateCrmContactCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var contact = await records.ContactAsync(request.Id, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (r.ExpectedUpdatedAt is { } expected)
        {
            await OptimisticConcurrency.EnsureUnchangedAsync(
                db.CrmContacts, contact.Id, expected, now, "crm_contact.modified", "联系人刚被其他人修改过，已刷新为最新内容，请确认后再保存。", cancellationToken)
                .ConfigureAwait(false);
        }

        await records.EnsureOwnerAsync(r.OwnerUserId, cancellationToken).ConfigureAwait(false);
        await records.EnsureCompanyExistsAsync(r.CompanyId, cancellationToken).ConfigureAwait(false);
        var previousCompany = contact.CompanyId;
        if (r.CompanyId is { } newCompany && newCompany != previousCompany)
        {
            await DetachFromOtherCompaniesDealsAsync(contact.Id, newCompany, now, cancellationToken).ConfigureAwait(false);
        }

        contact.Name = r.Name.Trim();
        contact.OwnerUserId = r.OwnerUserId;
        Apply(contact, r);
        contact.UpdatedAt = now;
        changes.Publish(CrmRealtime.Contact, contact.Id, RealtimeChangeKind.Updated);
        foreach (var companyId in new[] { previousCompany, contact.CompanyId }.OfType<Guid>().Distinct())
        {
            changes.Publish(CrmRealtime.Company, companyId, RealtimeChangeKind.Updated);
        }

        return (await CrmMapping.ContactsAsync(db, [contact], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>
    /// 删除联系人：相关商机保留（不再关联联系人），已同时记在客户或商机上的跟进记录保留，只属于这个联系人的一并删除。
    /// </summary>
    public async Task<Unit> HandleAsync(DeleteCrmContactCommand request, CancellationToken cancellationToken)
    {
        var contact = await records.ContactAsync(request.Id, cancellationToken).ConfigureAwait(false);
        await db.CrmActivities.Where(a => a.ContactId == contact.Id && a.CompanyId == null && a.DealId == null)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.CrmContacts.Remove(contact);
        changes.Publish(CrmRealtime.Contact, contact.Id, RealtimeChangeKind.Deleted);
        if (contact.CompanyId is { } companyId)
        {
            changes.Publish(CrmRealtime.Company, companyId, RealtimeChangeKind.Updated);
        }

        return Unit.Value;
    }

    /// <summary>
    /// 联系人换到另一个客户（例如跳槽）时，原客户的商机仍归原客户，只是不再由这个人对接：解除它与这些商机的关联，
    /// 保持"商机的联系人属于商机的客户"。
    /// </summary>
    private async Task DetachFromOtherCompaniesDealsAsync(Guid contactId, Guid newCompany, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var deals = db.CrmDeals.Where(d => d.ContactId == contactId && d.CompanyId != null && d.CompanyId != newCompany);
        var ids = await deals.Select(d => d.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            return;
        }

        await db.CrmDeals.Where(d => ids.Contains(d.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ContactId, (Guid?)null).SetProperty(d => d.UpdatedAt, now), cancellationToken).ConfigureAwait(false);
        foreach (var id in ids)
        {
            changes.Publish(CrmRealtime.Deal, id, RealtimeChangeKind.Updated);
        }
    }

    private static void Apply(CrmContact contact, SaveCrmContactRequest r)
    {
        contact.CompanyId = r.CompanyId;
        contact.Title = r.Title.Trim();
        contact.Email = r.Email.Trim();
        contact.Phone = r.Phone.Trim();
        contact.Description = r.Description.Trim();
        contact.Tags = CrmRecords.NormalizeTags(r.Tags);
    }
}

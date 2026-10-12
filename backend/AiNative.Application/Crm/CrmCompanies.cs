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
public sealed record ListCrmCompaniesQuery(string? Search = null, Guid? OwnerUserId = null, string? Tag = null, int Page = 1, int PageSize = 50)
    : IQuery<PagedResult<CrmCompanyDto>>;

[RequireFeature(CrmFeatures.View)]
public sealed record GetCrmCompanyQuery(Guid Id) : IQuery<CrmCompanyDetailDto>;

[RequireFeature(CrmFeatures.Manage)]
public sealed record CreateCrmCompanyCommand(SaveCrmCompanyRequest Request) : ICommand<CrmCompanyDto>;

[RequireFeature(CrmFeatures.Manage)]
public sealed record UpdateCrmCompanyCommand(Guid Id, SaveCrmCompanyRequest Request) : ICommand<CrmCompanyDto>;

[RequireFeature(CrmFeatures.Manage)]
public sealed record DeleteCrmCompanyCommand(Guid Id) : ICommand<Unit>;

public sealed class SaveCrmCompanyRequestValidator : AbstractValidator<SaveCrmCompanyRequest>
{
    public SaveCrmCompanyRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("请填写客户名称。").Text(200, "客户名称").OverridePropertyName("name");
        RuleFor(x => x.Industry).Text(100, "行业").OverridePropertyName("industry");
        RuleFor(x => x.Website).Text(300, "网站").OverridePropertyName("website");
        RuleFor(x => x.Phone).Text(50, "电话").OverridePropertyName("phone");
        RuleFor(x => x.Address).Text(500, "地址").OverridePropertyName("address");
        RuleFor(x => x.Description).Text(10_000, "简介").OverridePropertyName("description");
        this.Tags(x => x.Tags);
    }
}

public sealed class CreateCrmCompanyCommandValidator : AbstractValidator<CreateCrmCompanyCommand>
{
    public CreateCrmCompanyCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveCrmCompanyRequestValidator());
}

public sealed class UpdateCrmCompanyCommandValidator : AbstractValidator<UpdateCrmCompanyCommand>
{
    public UpdateCrmCompanyCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveCrmCompanyRequestValidator());
}

internal sealed class CrmCompanyHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    CrmRecords records,
    CrmCustomFields customFields,
    ITextMatcher text,
    IChangeFeed changes,
    TimeProvider clock) :
    IRequestHandler<ListCrmCompaniesQuery, PagedResult<CrmCompanyDto>>,
    IRequestHandler<GetCrmCompanyQuery, CrmCompanyDetailDto>,
    IRequestHandler<CreateCrmCompanyCommand, CrmCompanyDto>,
    IRequestHandler<UpdateCrmCompanyCommand, CrmCompanyDto>,
    IRequestHandler<DeleteCrmCompanyCommand, Unit>
{
    public async Task<PagedResult<CrmCompanyDto>> HandleAsync(ListCrmCompaniesQuery request, CancellationToken cancellationToken)
    {
        var (page, pageSize) = CrmRecords.Paging(request.Page, request.PageSize);
        var query = db.CrmCompanies.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            query = text.WhereContains(query, request.Search, c => c.Name, c => c.Industry, c => c.Phone);
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
        return new PagedResult<CrmCompanyDto>(await CrmMapping.CompaniesAsync(db, items, cancellationToken).ConfigureAwait(false), total, page, pageSize);
    }

    public async Task<CrmCompanyDetailDto> HandleAsync(GetCrmCompanyQuery request, CancellationToken cancellationToken)
    {
        var company = await db.CrmCompanies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.Id, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("crm_company", request.Id);
        var companyContacts = db.CrmContacts.AsNoTracking().Where(c => c.CompanyId == company.Id);
        var contacts = await companyContacts.OrderBy(c => c.Name)
            .Take(CrmRecords.RelatedLimit).ToListAsync(cancellationToken).ConfigureAwait(false);
        var companyDeals = db.CrmDeals.AsNoTracking().Where(d => d.CompanyId == company.Id);
        var deals = await companyDeals.OrderBy(d => d.Status).ThenByDescending(d => d.UpdatedAt)
            .Take(CrmRecords.RelatedLimit).ToListAsync(cancellationToken).ConfigureAwait(false);

        // 没取满时就是全部；取满了再数一次总数，调用方据此知道列表被截断了。
        var contactTotal = contacts.Count < CrmRecords.RelatedLimit ? contacts.Count : await companyContacts.CountAsync(cancellationToken).ConfigureAwait(false);
        var dealTotal = deals.Count < CrmRecords.RelatedLimit ? deals.Count : await companyDeals.CountAsync(cancellationToken).ConfigureAwait(false);
        var activities = await db.CrmActivities.AsNoTracking().Where(a => a.CompanyId == company.Id)
            .OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id)
            .Take(CrmRecords.TimelinePageSize).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new CrmCompanyDetailDto(
            (await CrmMapping.CompaniesAsync(db, [company], cancellationToken).ConfigureAwait(false))[0],
            await CrmMapping.ContactsAsync(db, contacts, cancellationToken).ConfigureAwait(false),
            await CrmMapping.DealsAsync(db, deals, cancellationToken).ConfigureAwait(false),
            await CrmMapping.ActivitiesAsync(db, activities, cancellationToken).ConfigureAwait(false),
            contactTotal,
            dealTotal);
    }

    public async Task<CrmCompanyDto> HandleAsync(CreateCrmCompanyCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var owner = records.DefaultOwner(r.OwnerUserId);
        await records.EnsureOwnerAsync(owner, cancellationToken).ConfigureAwait(false);
        var company = new CrmCompany
        {
            Name = r.Name.Trim(),
            CreatedBy = tenant.Actor,
            OwnerUserId = owner,
            CustomFields = await customFields.ForCreateAsync(CrmRecordType.Company, r.CustomFields, cancellationToken).ConfigureAwait(false),
        };
        Apply(company, r);
        db.CrmCompanies.Add(company);
        changes.Publish(CrmRealtime.Company, company.Id, RealtimeChangeKind.Created);
        var dto = (await CrmMapping.CompaniesAsync(db, [company], cancellationToken).ConfigureAwait(false))[0];
        await records.PublishAsync(CrmEventKeys.CompanyCreated, company.Id, () => Task.FromResult(new Dictionary<string, object?> { ["company"] = dto }), cancellationToken)
            .ConfigureAwait(false);
        return dto;
    }

    public async Task<CrmCompanyDto> HandleAsync(UpdateCrmCompanyCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var company = await records.CompanyAsync(request.Id, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (r.ExpectedUpdatedAt is { } expected)
        {
            await OptimisticConcurrency.EnsureUnchangedAsync(
                db.CrmCompanies, company.Id, expected, now, "crm_company.modified", "客户刚被其他人修改过，已刷新为最新内容，请确认后再保存。", cancellationToken)
                .ConfigureAwait(false);
        }

        await records.EnsureOwnerAsync(r.OwnerUserId, cancellationToken).ConfigureAwait(false);
        var values = await customFields.ForUpdateAsync(CrmRecordType.Company, r.CustomFields, cancellationToken).ConfigureAwait(false);
        company.Name = r.Name.Trim();
        company.OwnerUserId = r.OwnerUserId;
        company.CustomFields = values ?? company.CustomFields;
        Apply(company, r);

        // 显式写入，返回的 UpdatedAt 就是落库值，可直接作为下一次编辑的版本。
        company.UpdatedAt = now;
        changes.Publish(CrmRealtime.Company, company.Id, RealtimeChangeKind.Updated);
        return (await CrmMapping.CompaniesAsync(db, [company], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>
    /// 删除客户：名下联系人和商机保留下来（不再属于任何客户），只记在这个客户身上的跟进记录一并删除。
    /// </summary>
    public async Task<Unit> HandleAsync(DeleteCrmCompanyCommand request, CancellationToken cancellationToken)
    {
        var company = await records.CompanyAsync(request.Id, cancellationToken).ConfigureAwait(false);
        await db.CrmActivities.Where(a => a.CompanyId == company.Id && a.ContactId == null && a.DealId == null)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.CrmCompanies.Remove(company);
        changes.Publish(CrmRealtime.Company, company.Id, RealtimeChangeKind.Deleted);
        return Unit.Value;
    }

    private static void Apply(CrmCompany company, SaveCrmCompanyRequest r)
    {
        company.Industry = r.Industry.Trim();
        company.Website = r.Website.Trim();
        company.Phone = r.Phone.Trim();
        company.Address = r.Address.Trim();
        company.Description = r.Description.Trim();
        company.Tags = CrmRecords.NormalizeTags(r.Tags);
    }
}

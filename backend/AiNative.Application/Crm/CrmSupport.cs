using AiNative.Application.Abstractions;
using AiNative.Application.Common;
using AiNative.Application.Modules;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Common;
using AiNative.Contracts.Crm;
using AiNative.Domain.Common;
using AiNative.Domain.Crm;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Crm;

/// <summary>
/// 实体到 DTO 的批量映射：关联的名称、计数和操作者一次查齐，列表不逐条查询。
/// </summary>
internal static class CrmMapping
{
    public static async Task<IReadOnlyList<CrmCompanyDto>> CompaniesAsync(IAppDbContext db, IReadOnlyList<CrmCompany> companies, CancellationToken cancellationToken)
    {
        if (companies.Count == 0)
        {
            return [];
        }

        var ids = companies.Select(c => c.Id).ToList();
        var contactCounts = await db.CrmContacts.AsNoTracking()
            .Where(c => c.CompanyId.HasValue && ids.Contains(c.CompanyId.Value))
            .GroupBy(c => c.CompanyId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken)
            .ConfigureAwait(false);
        var dealCounts = await db.CrmDeals.AsNoTracking()
            .Where(d => d.Status == CrmDealStatus.Open && d.CompanyId.HasValue && ids.Contains(d.CompanyId.Value))
            .GroupBy(d => d.CompanyId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken)
            .ConfigureAwait(false);
        var actors = await ActorsAsync(db, companies.Select(c => c.CreatedBy), companies.Select(c => c.OwnerUserId), cancellationToken).ConfigureAwait(false);
        return
        [
            .. companies.Select(c => new CrmCompanyDto(
                c.Id,
                c.Name,
                c.Industry,
                c.Website,
                c.Phone,
                c.Address,
                c.Description,
                c.Tags,
                Owner(c.OwnerUserId, actors),
                contactCounts.GetValueOrDefault(c.Id),
                dealCounts.GetValueOrDefault(c.Id),
                actors(c.CreatedBy),
                c.CreatedAt,
                c.UpdatedAt,
                c.LastActivityAt,
                c.CustomFields)),
        ];
    }

    public static async Task<IReadOnlyList<CrmContactDto>> ContactsAsync(IAppDbContext db, IReadOnlyList<CrmContact> contacts, CancellationToken cancellationToken)
    {
        if (contacts.Count == 0)
        {
            return [];
        }

        var companies = await CompanyNamesAsync(db, contacts.Select(c => c.CompanyId), cancellationToken).ConfigureAwait(false);
        var actors = await ActorsAsync(db, contacts.Select(c => c.CreatedBy), contacts.Select(c => c.OwnerUserId), cancellationToken).ConfigureAwait(false);
        return
        [
            .. contacts.Select(c => new CrmContactDto(
                c.Id,
                c.Name,
                c.CompanyId,
                NameOf(companies, c.CompanyId),
                c.Title,
                c.Email,
                c.Phone,
                c.Description,
                c.Tags,
                Owner(c.OwnerUserId, actors),
                actors(c.CreatedBy),
                c.CreatedAt,
                c.UpdatedAt,
                c.LastActivityAt,
                c.CustomFields)),
        ];
    }

    public static async Task<IReadOnlyList<CrmDealDto>> DealsAsync(IAppDbContext db, IReadOnlyList<CrmDeal> deals, CancellationToken cancellationToken)
    {
        if (deals.Count == 0)
        {
            return [];
        }

        var stageIds = deals.Select(d => d.StageId).Distinct().ToList();
        var stages = await db.CrmStages.AsNoTracking().Where(s => stageIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, cancellationToken).ConfigureAwait(false);
        var companies = await CompanyNamesAsync(db, deals.Select(d => d.CompanyId), cancellationToken).ConfigureAwait(false);
        var contacts = await ContactNamesAsync(db, deals.Select(d => d.ContactId), cancellationToken).ConfigureAwait(false);
        var actors = await ActorsAsync(db, deals.Select(d => d.CreatedBy), deals.Select(d => d.OwnerUserId), cancellationToken).ConfigureAwait(false);
        return
        [
            .. deals.Select(d =>
            {
                var stage = stages.GetValueOrDefault(d.StageId);
                return new CrmDealDto(
                    d.Id,
                    d.Title,
                    d.CompanyId,
                    NameOf(companies, d.CompanyId),
                    d.ContactId,
                    NameOf(contacts, d.ContactId),
                    d.StageId,
                    stage?.Name ?? string.Empty,
                    d.Status,
                    stage?.Probability ?? 0,
                    d.Amount,
                    d.Currency,
                    d.ExpectedCloseDate,
                    d.Description,
                    Owner(d.OwnerUserId, actors),
                    actors(d.CreatedBy),
                    d.CreatedAt,
                    d.UpdatedAt,
                    d.ClosedAt,
                    d.LastActivityAt,
                    d.CustomFields);
            }),
        ];
    }

    public static async Task<IReadOnlyList<CrmActivityDto>> ActivitiesAsync(IAppDbContext db, IReadOnlyList<CrmActivity> activities, CancellationToken cancellationToken)
    {
        if (activities.Count == 0)
        {
            return [];
        }

        var companies = await CompanyNamesAsync(db, activities.Select(a => a.CompanyId), cancellationToken).ConfigureAwait(false);
        var contacts = await ContactNamesAsync(db, activities.Select(a => a.ContactId), cancellationToken).ConfigureAwait(false);
        var dealIds = activities.Where(a => a.DealId.HasValue).Select(a => a.DealId!.Value).Distinct().ToList();
        var deals = dealIds.Count == 0
            ? []
            : await db.CrmDeals.AsNoTracking().Where(d => dealIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.Title, cancellationToken).ConfigureAwait(false);
        var actors = await ActorNames.ResolveAsync(db, activities.Select(a => a.Author), cancellationToken).ConfigureAwait(false);
        return
        [
            .. activities.Select(a => new CrmActivityDto(
                a.Id,
                a.Kind,
                a.Content,
                a.OccurredAt,
                a.CompanyId,
                NameOf(companies, a.CompanyId),
                a.ContactId,
                NameOf(contacts, a.ContactId),
                a.DealId,
                NameOf(deals, a.DealId),
                actors(a.Author),
                a.CreatedAt)),
        ];
    }

    public static CrmStageDto ToDto(this CrmStage stage) => new(stage.Id, stage.Name, stage.Position, stage.Probability, stage.Kind);

    public static CrmFieldDto ToDto(this CrmFieldDefinition field)
        => new(field.Id, field.RecordType, field.Key, field.Label, field.Type, field.Options, field.Required, field.Position, field.Description);

    private static Task<Func<Actor, ActorDto>> ActorsAsync(IAppDbContext db, IEnumerable<Actor> creators, IEnumerable<Guid?> owners, CancellationToken cancellationToken)
        => ActorNames.ResolveAsync(db, creators.Concat(owners.Where(o => o.HasValue).Select(o => Actor.ForUser(o!.Value))), cancellationToken);

    private static ActorDto? Owner(Guid? userId, Func<Actor, ActorDto> actors) => userId is { } id ? actors(Actor.ForUser(id)) : null;

    private static string? NameOf(Dictionary<Guid, string> names, Guid? id) => id is { } key ? names.GetValueOrDefault(key) : null;

    private static async Task<Dictionary<Guid, string>> CompanyNamesAsync(IAppDbContext db, IEnumerable<Guid?> ids, CancellationToken cancellationToken)
    {
        var list = ids.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
        return list.Count == 0
            ? []
            : await db.CrmCompanies.AsNoTracking().Where(c => list.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Dictionary<Guid, string>> ContactNamesAsync(IAppDbContext db, IEnumerable<Guid?> ids, CancellationToken cancellationToken)
    {
        var list = ids.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
        return list.Count == 0
            ? []
            : await db.CrmContacts.AsNoTracking().Where(c => list.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// 客户管理各处理器共用的校验与写入辅助。
/// </summary>
internal sealed class CrmRecords(IAppDbContext db, ITenantContext tenant, IPlatformEvents events, IChangeFeed changes)
{
    public const int TimelinePageSize = 50;

    /// <summary>
    /// 详情里最多附带这么多联系人、商机；总数另行返回。
    /// </summary>
    public const int RelatedLimit = 200;

    public async Task EnsureOwnerAsync(Guid? userId, CancellationToken cancellationToken)
    {
        if (userId is { } id && !await db.Memberships.AnyAsync(m => m.UserId == id, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("member", id);
        }
    }

    /// <summary>
    /// 新建记录没指定负责人时，由创建者（成员）负责；智能体创建的记录默认没有负责人。
    /// </summary>
    public Guid? DefaultOwner(Guid? requested) => requested ?? (tenant.Actor.IsUser ? tenant.Actor.Id : null);

    public async Task<CrmCompany> CompanyAsync(Guid id, CancellationToken cancellationToken)
        => await db.CrmCompanies.FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false)
           ?? throw new NotFoundException("crm_company", id);

    public async Task<CrmContact> ContactAsync(Guid id, CancellationToken cancellationToken)
        => await db.CrmContacts.FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false)
           ?? throw new NotFoundException("crm_contact", id);

    public async Task<CrmDeal> DealAsync(Guid id, CancellationToken cancellationToken)
        => await db.CrmDeals.FirstOrDefaultAsync(d => d.Id == id, cancellationToken).ConfigureAwait(false)
           ?? throw new NotFoundException("crm_deal", id);

    public async Task EnsureCompanyExistsAsync(Guid? id, CancellationToken cancellationToken)
    {
        if (id is { } companyId && !await db.CrmCompanies.AnyAsync(c => c.Id == companyId, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("crm_company", companyId);
        }
    }

    /// <summary>
    /// 联系人换到另一个客户（例如跳槽）时，原客户的商机仍归原客户，只是不再由这个人对接：解除它与这些商机的关联，
    /// 保持"商机的联系人属于商机的客户"。
    /// </summary>
    public async Task DetachContactFromOtherCompaniesDealsAsync(Guid contactId, Guid newCompany, DateTimeOffset now, CancellationToken cancellationToken)
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

    /// <summary>
    /// 推进关联记录的"最近跟进时间"（只会往后推）。不算编辑，不改 UpdatedAt，不影响别人手里的编辑版本。
    /// </summary>
    public async Task TouchAsync(Guid? companyId, Guid? contactId, Guid? dealId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (companyId is { } company)
        {
            await db.CrmCompanies.Where(c => c.Id == company && (c.LastActivityAt == null || c.LastActivityAt < at))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastActivityAt, at), cancellationToken).ConfigureAwait(false);
        }

        if (contactId is { } contact)
        {
            await db.CrmContacts.Where(c => c.Id == contact && (c.LastActivityAt == null || c.LastActivityAt < at))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastActivityAt, at), cancellationToken).ConfigureAwait(false);
        }

        if (dealId is { } deal)
        {
            await db.CrmDeals.Where(d => d.Id == deal && (d.LastActivityAt == null || d.LastActivityAt < at))
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastActivityAt, at), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 删除跟进记录后，按剩下的记录重新计算关联记录的"最近跟进时间"（没有剩余记录时清空）。
    /// </summary>
    /// <param name="deletedId">正在删除、尚未落库的那条记录，计算时排除。</param>
    public async Task RecomputeLastActivityAsync(Guid? companyId, Guid? contactId, Guid? dealId, Guid deletedId, CancellationToken cancellationToken)
    {
        var remaining = db.CrmActivities.Where(a => a.Id != deletedId);
        if (companyId is { } company)
        {
            await db.CrmCompanies.Where(c => c.Id == company)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastActivityAt, c => remaining.Where(a => a.CompanyId == c.Id).Max(a => (DateTimeOffset?)a.OccurredAt)), cancellationToken)
                .ConfigureAwait(false);
        }

        if (contactId is { } contact)
        {
            await db.CrmContacts.Where(c => c.Id == contact)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastActivityAt, c => remaining.Where(a => a.ContactId == c.Id).Max(a => (DateTimeOffset?)a.OccurredAt)), cancellationToken)
                .ConfigureAwait(false);
        }

        if (dealId is { } deal)
        {
            await db.CrmDeals.Where(d => d.Id == deal)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastActivityAt, d => remaining.Where(a => a.DealId == d.Id).Max(a => (DateTimeOffset?)a.OccurredAt)), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 发布客户管理事件（订阅的工作流随本事务创建运行）。载荷只在有订阅者时构造，自动带上操作者。
    /// </summary>
    public Task PublishAsync(string eventType, Guid subjectId, Func<Task<Dictionary<string, object?>>> payload, CancellationToken cancellationToken)
        => events.PublishAsync(eventType, subjectId, async () =>
        {
            var data = await payload().ConfigureAwait(false);
            var actors = await ActorNames.ResolveAsync(db, [tenant.Actor], cancellationToken).ConfigureAwait(false);
            data["actor"] = actors(tenant.Actor);
            return data;
        }, cancellationToken);

    public static (int Page, int PageSize) Paging(int page, int pageSize) => Common.Paging.Normalize(page, pageSize);

    /// <summary>
    /// 去掉首尾空白、空标签和重复标签（不区分大小写，保留第一次出现的写法）。
    /// </summary>
    public static List<string> NormalizeTags(IEnumerable<string> tags)
        => [.. tags.Select(t => t.Trim()).Where(t => t.Length > 0).DistinctBy(t => t.ToUpperInvariant())];

    /// <summary>
    /// 把一段文字拆成多个值（标签、多选项）：逗号、顿号、分号、竖线和换行都算分隔符，中英文标点都可以。
    /// </summary>
    public static IEnumerable<string> SplitList(string text)
        => text.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static readonly char[] ListSeparators = [',', '，', '、', ';', '；', '|', '\n', '\r'];
}

internal static class CrmValidation
{
    public const int MaxTags = 20;

    public static void Tags<T>(this AbstractValidator<T> validator, System.Linq.Expressions.Expression<Func<T, IEnumerable<string>>> tags)
    {
        validator.RuleFor(tags).Must(t => t.Count() <= MaxTags).WithMessage($"标签最多 {MaxTags} 个。").OverridePropertyName("tags");
        validator.RuleForEach(tags).MaximumLength(30).WithMessage("单个标签最多 30 个字。").OverridePropertyName("tags");
    }
}

using System.Text.Json;
using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Application.Files;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Crm;
using AiNative.Domain.Crm;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AiNative.Application.Crm;

/// <summary>
/// 读取上传的表格，返回列、示例值和按表头猜出的对应字段，供用户确认后再导入。不保存任何内容。
/// </summary>
[RequireFeature(CrmFeatures.Manage)]
public sealed record PreviewCrmImportQuery(CrmRecordType RecordType, string FileName, long Length, Stream Content) : IQuery<CrmImportPreviewDto>;

/// <summary>
/// 按对应关系导入表格里的客户或联系人。有问题的行跳过并报告原因，其余行照常导入；DryRun 时只检查不保存。
/// 导入的记录不触发「新客户」「新联系人」等工作流事件，避免一次导入启动成千上万个运行。
/// </summary>
[RequireFeature(CrmFeatures.Manage)]
public sealed record ImportCrmRecordsCommand(CrmImportOptions Options, string FileName, long Length, Stream Content) : IRequest<CrmImportResultDto>;

public static class CrmImportLimits
{
    public const long MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxRows = 5_000;
    public const int MaxColumns = 100;
    public const int MaxReportedErrors = 200;
}

public sealed class PreviewCrmImportQueryValidator : AbstractValidator<PreviewCrmImportQuery>
{
    public PreviewCrmImportQueryValidator() => RuleFor(x => x.RecordType).Must(CrmImportTargets.Supports).WithMessage("目前只能导入客户和联系人。").OverridePropertyName("recordType");
}

public sealed class ImportCrmRecordsCommandValidator : AbstractValidator<ImportCrmRecordsCommand>
{
    public ImportCrmRecordsCommandValidator()
    {
        RuleFor(x => x.Options.RecordType).Must(CrmImportTargets.Supports).WithMessage("目前只能导入客户和联系人。").OverridePropertyName("recordType");
        RuleFor(x => x.Options.Duplicates).IsInEnum().WithMessage("重复记录的处理方式不正确。").OverridePropertyName("duplicates");
        RuleFor(x => x.Options.Mapping).Must(m => m.Count <= CrmImportLimits.MaxColumns).WithMessage($"最多 {CrmImportLimits.MaxColumns} 列。").OverridePropertyName("mapping");
    }
}

/// <summary>
/// 导入时可以对应的字段：标准字段加上这类记录的自定义字段（键为 custom.字段标识）。
/// </summary>
public static class CrmImportTargets
{
    public const string CustomPrefix = "custom.";

    private static readonly string[] PhoneAliases = ["联系电话", "电话号码", "手机", "手机号", "手机号码", "座机", "phone", "tel", "telephone", "mobile"];
    private static readonly string[] TagAliases = ["tags", "tag", "分类"];
    private static readonly string[] OwnerAliases = ["所有者", "跟进人", "销售", "客户经理", "owner", "assignedto"];
    private const string OwnerHint = "填成员的邮箱或姓名，不填时由导入的人负责";
    private const string TagHint = "多个标签用逗号、顿号或分号分隔";

    private static readonly Target[] CompanyTargets =
    [
        new("name", "客户名称", true, "按名称判断客户是否已存在",
            ["客户", "客户名", "公司", "公司名", "公司名称", "企业", "企业名称", "单位", "单位名称", "名称", "company", "companyname", "account", "accountname", "organization", "name"]),
        new("industry", "行业", false, null, ["所属行业", "industry"]),
        new("website", "网站", false, null, ["网址", "官网", "主页", "website", "url", "web", "site"]),
        new("phone", "电话", false, null, PhoneAliases),
        new("address", "地址", false, null, ["公司地址", "详细地址", "address"]),
        new("description", "简介", false, null, ["备注", "描述", "说明", "介绍", "description", "notes", "note", "remark", "remarks"]),
        new("tags", "标签", false, TagHint, TagAliases),
        new("owner", "负责人", false, OwnerHint, OwnerAliases),
    ];

    private static readonly Target[] ContactTargets =
    [
        new("name", "姓名", true, null, ["名字", "联系人", "联系人姓名", "name", "fullname", "contact", "contactname"]),
        new("company", "所属客户", false, "按客户名称匹配已有客户",
            ["客户", "客户名称", "公司", "公司名", "公司名称", "单位", "单位名称", "企业", "企业名称", "company", "companyname", "account", "organization"]),
        new("title", "职位", false, null, ["职务", "头衔", "岗位", "title", "jobtitle", "position"]),
        new("email", "邮箱", false, "按邮箱判断联系人是否已存在", ["邮件", "电子邮件", "电子邮箱", "邮箱地址", "email", "mail", "emailaddress"]),
        new("phone", "电话", false, null, PhoneAliases),
        new("description", "备注", false, null, ["描述", "说明", "description", "notes", "note", "remark", "remarks"]),
        new("tags", "标签", false, TagHint, TagAliases),
        new("owner", "负责人", false, OwnerHint, OwnerAliases),
    ];

    public static bool Supports(CrmRecordType recordType) => recordType is CrmRecordType.Company or CrmRecordType.Contact;

    public static IReadOnlyList<CrmImportTargetDto> For(CrmRecordType recordType, IReadOnlyList<CrmFieldDefinition> fields)
        =>
        [
            .. Standard(recordType).Select(t => new CrmImportTargetDto(t.Key, t.Label, t.Required, t.Hint)),
            .. fields.Select(f => new CrmImportTargetDto(CustomPrefix + f.Key, f.Label, f.Required, Hint(f))),
        ];

    /// <summary>
    /// 按表头猜每一列对应的字段：表头与字段名称、常见别名或自定义字段标识一致（忽略大小写、空格和标点）。每个字段最多对应一列。
    /// </summary>
    public static IReadOnlyList<string?> Suggest(CrmRecordType recordType, IReadOnlyList<string> headers, IReadOnlyList<CrmFieldDefinition> fields)
    {
        var candidates = new List<(string Key, HashSet<string> Names)>();
        foreach (var target in Standard(recordType))
        {
            candidates.Add((target.Key, [Normalize(target.Label), .. target.Aliases.Select(Normalize)]));
        }

        foreach (var field in fields)
        {
            candidates.Add((CustomPrefix + field.Key, [Normalize(field.Label), Normalize(field.Key)]));
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new string?[headers.Count];

        // 先按字段名称精确匹配，再按别名，避免「客户」这样的别名抢走「客户名称」列。
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < headers.Count; i++)
            {
                if (result[i] is not null)
                {
                    continue;
                }

                var header = Normalize(headers[i]);
                var match = candidates.FirstOrDefault(c => !used.Contains(c.Key) && (pass == 0 ? c.Names.First() == header : c.Names.Contains(header)));
                if (match.Key is not null)
                {
                    result[i] = match.Key;
                    used.Add(match.Key);
                }
            }
        }

        return result;
    }

    private static Target[] Standard(CrmRecordType recordType) => recordType == CrmRecordType.Company ? CompanyTargets : ContactTargets;

    private static string? Hint(CrmFieldDefinition field) => field.Type switch
    {
        CrmFieldType.Number => "数字",
        CrmFieldType.Date => "日期，例如 2026-10-12",
        CrmFieldType.Select => "可选：" + Options(field),
        CrmFieldType.MultiSelect => "可多选，用逗号分隔：" + Options(field),
        CrmFieldType.Checkbox => "填“是”或“否”",
        _ => null,
    };

    private static string Options(CrmFieldDefinition field)
        => string.Join("、", field.Options.Take(10)) + (field.Options.Count > 10 ? " 等" : string.Empty);

    private static string Normalize(string text)
        => string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c is not ('_' or '-' or '*' or ':' or '：' or '.' or '(' or ')' or '（' or '）'))).ToLowerInvariant();

    private sealed record Target(string Key, string Label, bool Required, string? Hint, string[] Aliases);
}

internal sealed class CrmImportHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    CrmRecords records,
    CrmCustomFields customFields,
    ITabularFileReader reader,
    ITransactionLock locks,
    IUnitOfWork unitOfWork,
    IChangeFeed changes,
    TimeProvider clock,
    ILogger<CrmImportHandlers> logger) :
    IRequestHandler<PreviewCrmImportQuery, CrmImportPreviewDto>,
    IRequestHandler<ImportCrmRecordsCommand, CrmImportResultDto>
{
    private const int SampleCount = 3;
    private const int SampleLength = 100;

    public async Task<CrmImportPreviewDto> HandleAsync(PreviewCrmImportQuery request, CancellationToken cancellationToken)
    {
        var (fileName, data) = await ReadAsync(request.FileName, request.Length, request.Content, cancellationToken).ConfigureAwait(false);
        var fields = await customFields.DefinitionsAsync(request.RecordType, cancellationToken).ConfigureAwait(false);
        var suggested = CrmImportTargets.Suggest(request.RecordType, data.Headers, fields);
        var columns = data.Headers.Select((header, i) => new CrmImportColumnDto(
                i,
                header,
                [.. data.Rows.Select(r => r.Cell(i).Trim()).Where(v => v.Length > 0).Take(SampleCount).Select(v => v.Length > SampleLength ? v[..SampleLength] + "…" : v)],
                suggested[i]))
            .ToList();
        return new CrmImportPreviewDto(request.RecordType, fileName, data.Rows.Count, columns, CrmImportTargets.For(request.RecordType, fields));
    }

    public async Task<CrmImportResultDto> HandleAsync(ImportCrmRecordsCommand request, CancellationToken cancellationToken)
    {
        var options = request.Options;
        var (_, data) = await ReadAsync(request.FileName, request.Length, request.Content, cancellationToken).ConfigureAwait(false);
        var fields = await customFields.DefinitionsAsync(options.RecordType, cancellationToken).ConfigureAwait(false);
        var mapping = Mapping(options, data.Headers.Count, CrmImportTargets.For(options.RecordType, fields));
        var rows = data.Rows.Select(r => ImportRow.From(r, mapping)).ToList();

        // 同一空间的导入串行执行：查重基于最新数据，同一份文件连点两次也不会导入两遍。
        return await unitOfWork.RunAsync(async () =>
        {
            await locks.AcquireAsync($"crm-import:{tenant.TenantId}", cancellationToken).ConfigureAwait(false);
            var members = mapping.ContainsKey("owner") ? await MembersAsync(cancellationToken).ConfigureAwait(false) : null;
            var outcome = new ImportOutcome(options.DryRun, rows.Count);
            if (options.RecordType == CrmRecordType.Company)
            {
                await ImportCompaniesAsync(rows, fields, options, members, outcome, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ImportContactsAsync(rows, fields, options, members, outcome, cancellationToken).ConfigureAwait(false);
            }

            return outcome.ToDto();
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ImportCompaniesAsync(
        List<ImportRow> rows,
        IReadOnlyList<CrmFieldDefinition> fields,
        CrmImportOptions options,
        MemberDirectory? members,
        ImportOutcome outcome,
        CancellationToken cancellationToken)
    {
        var existing = options.Duplicates == CrmImportDuplicateMode.Create
            ? []
            : await CompaniesByNameAsync(rows.Select(r => r.Get("name")), tracked: !options.DryRun, cancellationToken).ConfigureAwait(false);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var validator = new SaveCrmCompanyRequestValidator();
        var now = clock.GetUtcNow();
        foreach (var row in rows)
        {
            if (row.IsEmpty)
            {
                outcome.Skipped++;
                continue;
            }

            if (row.Get("name") is not { } name)
            {
                outcome.Fail(row, ["没有填写客户名称。"]);
                continue;
            }

            var key = Key(name);
            if (options.Duplicates != CrmImportDuplicateMode.Create && !seen.TryAdd(key, row.Number))
            {
                outcome.Fail(row, [$"和第 {seen[key]} 行是同一个客户。"]);
                continue;
            }

            var target = existing.GetValueOrDefault(key);
            if (target is not null && options.Duplicates == CrmImportDuplicateMode.Skip)
            {
                outcome.Skipped++;
                continue;
            }

            var errors = new List<string>();
            var owner = Owner(row, members, errors);
            var save = target is null
                ? new SaveCrmCompanyRequest
                {
                    Name = name,
                    Industry = row.Get("industry") ?? string.Empty,
                    Website = row.Get("website") ?? string.Empty,
                    Phone = row.Get("phone") ?? string.Empty,
                    Address = row.Get("address") ?? string.Empty,
                    Description = row.Get("description") ?? string.Empty,
                    Tags = Tags(row, []),
                }
                : new SaveCrmCompanyRequest
                {
                    Name = target.Name,
                    Industry = row.Get("industry") ?? target.Industry,
                    Website = row.Get("website") ?? target.Website,
                    Phone = row.Get("phone") ?? target.Phone,
                    Address = row.Get("address") ?? target.Address,
                    Description = row.Get("description") ?? target.Description,
                    Tags = Tags(row, target.Tags),
                };
            errors.AddRange(validator.Validate(save).Errors.Select(e => e.ErrorMessage).Distinct());
            var values = CustomValues(fields, target?.CustomFields, row, errors);
            if (errors.Count > 0)
            {
                outcome.Fail(row, errors);
                continue;
            }

            if (target is null)
            {
                outcome.Created++;
                if (!options.DryRun)
                {
                    db.CrmCompanies.Add(new CrmCompany
                    {
                        Name = save.Name,
                        Industry = save.Industry,
                        Website = save.Website,
                        Phone = save.Phone,
                        Address = save.Address,
                        Description = save.Description,
                        Tags = [.. save.Tags],
                        OwnerUserId = owner ?? records.DefaultOwner(null),
                        CreatedBy = tenant.Actor,
                        CustomFields = values,
                    });
                }
            }
            else
            {
                outcome.Updated++;
                if (!options.DryRun)
                {
                    target.Industry = save.Industry;
                    target.Website = save.Website;
                    target.Phone = save.Phone;
                    target.Address = save.Address;
                    target.Description = save.Description;
                    target.Tags = [.. save.Tags];
                    target.OwnerUserId = owner ?? target.OwnerUserId;
                    target.CustomFields = values;
                    target.UpdatedAt = now;
                }
            }
        }

        if (!options.DryRun && outcome.Created + outcome.Updated > 0)
        {
            changes.Publish(CrmRealtime.Company, tenant.TenantId, RealtimeChangeKind.Updated);
        }
    }

    private async Task ImportContactsAsync(
        List<ImportRow> rows,
        IReadOnlyList<CrmFieldDefinition> fields,
        CrmImportOptions options,
        MemberDirectory? members,
        ImportOutcome outcome,
        CancellationToken cancellationToken)
    {
        var existing = new Dictionary<string, CrmContact>(StringComparer.Ordinal);
        var emails = rows.Select(r => r.Get("email")).OfType<string>().Select(Key).Distinct().ToList();
        if (options.Duplicates != CrmImportDuplicateMode.Create && emails.Count > 0)
        {
            var query = db.CrmContacts.Where(c => emails.Contains(c.Email.ToLower()));
            var matches = await (options.DryRun ? query.AsNoTracking() : query)
                .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var contact in matches)
            {
                existing.TryAdd(Key(contact.Email), contact);
            }
        }

        var companies = (await CompaniesByNameAsync(rows.Select(r => r.Get("company")), tracked: false, cancellationToken).ConfigureAwait(false))
            .ToDictionary(c => c.Key, c => c.Value.Id, StringComparer.Ordinal);
        var newCompanies = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var requiredCompanyFields = options.CreateMissingCompanies
            ? (await customFields.DefinitionsAsync(CrmRecordType.Company, cancellationToken).ConfigureAwait(false)).Where(f => f.Required).Select(f => f.Label).ToList()
            : [];
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var validator = new SaveCrmContactRequestValidator();
        var now = clock.GetUtcNow();
        foreach (var row in rows)
        {
            if (row.IsEmpty)
            {
                outcome.Skipped++;
                continue;
            }

            if (row.Get("name") is not { } name)
            {
                outcome.Fail(row, ["没有填写姓名。"]);
                continue;
            }

            var email = row.Get("email");
            var emailKey = email is null ? null : Key(email);
            if (emailKey is not null && options.Duplicates != CrmImportDuplicateMode.Create && !seen.TryAdd(emailKey, row.Number))
            {
                outcome.Fail(row, [$"和第 {seen[emailKey]} 行的邮箱相同。"]);
                continue;
            }

            var target = emailKey is null ? null : existing.GetValueOrDefault(emailKey);
            if (target is not null && options.Duplicates == CrmImportDuplicateMode.Skip)
            {
                outcome.Skipped++;
                continue;
            }

            var errors = new List<string>();
            Guid? companyId = target?.CompanyId;
            string? companyToCreate = null;
            if (row.Get("company") is { } companyName)
            {
                var companyKey = Key(companyName);
                if (companies.TryGetValue(companyKey, out var found) || newCompanies.TryGetValue(companyKey, out found))
                {
                    companyId = found;
                }
                else if (!options.CreateMissingCompanies)
                {
                    errors.Add($"找不到客户「{companyName}」，可以选择自动新建不存在的客户。");
                }
                else if (requiredCompanyFields.Count > 0)
                {
                    errors.Add($"找不到客户「{companyName}」；客户有必填字段（{string.Join("、", requiredCompanyFields)}），不能自动新建，请先导入客户。");
                }
                else if (companyName.Length > 200)
                {
                    errors.Add("客户名称最多 200 个字。");
                }
                else
                {
                    companyToCreate = companyName;
                }
            }

            var owner = Owner(row, members, errors);
            var save = target is null
                ? new SaveCrmContactRequest
                {
                    Name = name,
                    Title = row.Get("title") ?? string.Empty,
                    Email = email ?? string.Empty,
                    Phone = row.Get("phone") ?? string.Empty,
                    Description = row.Get("description") ?? string.Empty,
                    Tags = Tags(row, []),
                }
                : new SaveCrmContactRequest
                {
                    Name = name,
                    Title = row.Get("title") ?? target.Title,
                    Email = target.Email,
                    Phone = row.Get("phone") ?? target.Phone,
                    Description = row.Get("description") ?? target.Description,
                    Tags = Tags(row, target.Tags),
                };
            errors.AddRange(validator.Validate(save).Errors.Select(e => e.ErrorMessage).Distinct());
            var values = CustomValues(fields, target?.CustomFields, row, errors);
            if (errors.Count > 0)
            {
                outcome.Fail(row, errors);
                continue;
            }

            if (companyToCreate is not null)
            {
                var company = new CrmCompany { Name = companyToCreate, CreatedBy = tenant.Actor, OwnerUserId = owner ?? records.DefaultOwner(null) };
                newCompanies[Key(companyToCreate)] = company.Id;
                companyId = company.Id;
                outcome.CompaniesCreated++;
                if (!options.DryRun)
                {
                    db.CrmCompanies.Add(company);
                }
            }

            if (target is null)
            {
                outcome.Created++;
                if (!options.DryRun)
                {
                    db.CrmContacts.Add(new CrmContact
                    {
                        Name = save.Name,
                        CompanyId = companyId,
                        Title = save.Title,
                        Email = save.Email,
                        Phone = save.Phone,
                        Description = save.Description,
                        Tags = [.. save.Tags],
                        OwnerUserId = owner ?? records.DefaultOwner(null),
                        CreatedBy = tenant.Actor,
                        CustomFields = values,
                    });
                }
            }
            else
            {
                outcome.Updated++;
                if (!options.DryRun)
                {
                    if (companyId is { } newCompany && newCompany != target.CompanyId)
                    {
                        await records.DetachContactFromOtherCompaniesDealsAsync(target.Id, newCompany, now, cancellationToken).ConfigureAwait(false);
                    }

                    target.Name = save.Name;
                    target.CompanyId = companyId;
                    target.Title = save.Title;
                    target.Phone = save.Phone;
                    target.Description = save.Description;
                    target.Tags = [.. save.Tags];
                    target.OwnerUserId = owner ?? target.OwnerUserId;
                    target.CustomFields = values;
                    target.UpdatedAt = now;
                }
            }
        }

        if (!options.DryRun && outcome.Created + outcome.Updated > 0)
        {
            changes.Publish(CrmRealtime.Contact, tenant.TenantId, RealtimeChangeKind.Updated);
            changes.Publish(CrmRealtime.Company, tenant.TenantId, RealtimeChangeKind.Updated);
        }
    }

    private async Task<(string FileName, TabularData Data)> ReadAsync(string fileName, long length, Stream content, CancellationToken cancellationToken)
    {
        var name = UploadedFiles.SafeName(fileName);
        if (length == 0)
        {
            throw new TabularFileException("file.empty", "文件是空的。");
        }

        await using var buffer = await UploadedFiles.ReadAsync(
            content,
            length,
            CrmImportLimits.MaxFileBytes,
            () => new TabularFileException("file.too_large", $"文件超过 {CrmImportLimits.MaxFileBytes / 1024 / 1024} MB，请拆分后再导入。"),
            cancellationToken).ConfigureAwait(false);
        try
        {
            return (name, reader.Read(name, buffer, new TabularLimits(CrmImportLimits.MaxRows, CrmImportLimits.MaxColumns), cancellationToken));
        }
        catch (Exception ex) when (ex is not (TabularFileException or OperationCanceledException))
        {
            // 解析库对畸形文件抛出的异常五花八门，统一告诉用户文件读不了，细节留在日志里。
            logger.LogWarning(ex, "Failed to read import file {FileName}", name);
            throw new TabularFileException("file.unreadable", "无法读取这个文件，可能已损坏或格式不正确。");
        }
    }

    /// <summary>
    /// 校验列与字段的对应关系，返回「字段 → 列号」。
    /// </summary>
    private static Dictionary<string, int> Mapping(CrmImportOptions options, int columns, IReadOnlyList<CrmImportTargetDto> targets)
    {
        if (options.Mapping.Count > columns)
        {
            throw Invalid("列的对应关系和文件不一致，请重新选择文件。");
        }

        var labels = targets.ToDictionary(t => t.Key, t => t.Label, StringComparer.Ordinal);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < options.Mapping.Count; i++)
        {
            var target = options.Mapping[i]?.Trim();
            if (string.IsNullOrEmpty(target))
            {
                continue;
            }

            if (!labels.TryGetValue(target, out var label))
            {
                throw Invalid($"没有「{target}」这个字段，字段可能刚被删除，请重新选择。");
            }

            if (!result.TryAdd(target, i))
            {
                throw Invalid($"「{label}」对应了多列，每个字段只能对应一列。");
            }
        }

        if (!result.ContainsKey("name"))
        {
            throw Invalid($"请指定哪一列是{labels["name"]}。");
        }

        return result;
    }

    private static ValidationFailedException Invalid(string message) => new(new Dictionary<string, string[]> { ["mapping"] = [message] });

    private async Task<Dictionary<string, CrmCompany>> CompaniesByNameAsync(IEnumerable<string?> names, bool tracked, CancellationToken cancellationToken)
    {
        var keys = names.OfType<string>().Select(Key).Distinct().ToList();
        if (keys.Count == 0)
        {
            return [];
        }

        // 重名时取最早建的那个，结果稳定。
        var query = db.CrmCompanies.Where(c => keys.Contains(c.Name.ToLower()));
        var companies = await (tracked ? query : query.AsNoTracking())
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, CrmCompany>(StringComparer.Ordinal);
        foreach (var company in companies)
        {
            result.TryAdd(Key(company.Name), company);
        }

        return result;
    }

    private async Task<MemberDirectory> MembersAsync(CancellationToken cancellationToken)
    {
        var members = await (from m in db.Memberships.AsNoTracking()
                             join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                             select new MemberDirectory.Member(u.Id, u.Email, u.DisplayName))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new MemberDirectory(members);
    }

    private static Guid? Owner(ImportRow row, MemberDirectory? members, List<string> errors)
    {
        if (members is null || row.Get("owner") is not { } text)
        {
            return null;
        }

        var (id, error) = members.Find(text);
        if (error is not null)
        {
            errors.Add(error);
        }

        return id;
    }

    private static List<string> Tags(ImportRow row, IEnumerable<string> current)
        => CrmRecords.NormalizeTags(row.Get("tags") is { } text ? current.Concat(CrmRecords.SplitList(text)) : current);

    private static Dictionary<string, JsonElement> CustomValues(IReadOnlyList<CrmFieldDefinition> fields, Dictionary<string, JsonElement>? current, ImportRow row, List<string> errors)
    {
        var fieldErrors = new Dictionary<string, string>(StringComparer.Ordinal);
        var values = CrmFieldValues.Merge(fields, current, row.Custom, fieldErrors);
        errors.AddRange(fieldErrors.Values);
        return values;
    }

    private static string Key(string text) => text.Trim().ToLowerInvariant();

    /// <summary>
    /// 一行里有值的单元格：标准字段按名称，自定义字段按标识（值保持原文，由取值规范统一转换）。
    /// </summary>
    private sealed record ImportRow(int Number, Dictionary<string, string> Values, Dictionary<string, JsonElement> Custom)
    {
        public bool IsEmpty => Values.Count == 0 && Custom.Count == 0;

        public string? Get(string key) => Values.GetValueOrDefault(key);

        public static ImportRow From(TabularRow row, Dictionary<string, int> mapping)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var custom = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var (target, column) in mapping)
            {
                var cell = row.Cell(column).Trim();
                if (cell.Length == 0)
                {
                    continue;
                }

                if (target.StartsWith(CrmImportTargets.CustomPrefix, StringComparison.Ordinal))
                {
                    custom[target[CrmImportTargets.CustomPrefix.Length..]] = JsonSerializer.SerializeToElement(cell, PlatformJson.Options);
                }
                else
                {
                    values[target] = cell;
                }
            }

            return new ImportRow(row.Number, values, custom);
        }
    }

    private sealed class MemberDirectory(List<MemberDirectory.Member> members)
    {
        public (Guid? Id, string? Error) Find(string text)
        {
            var byEmail = members.FirstOrDefault(m => string.Equals(m.Email, text, StringComparison.OrdinalIgnoreCase));
            if (byEmail is not null)
            {
                return (byEmail.Id, null);
            }

            var byName = members.Where(m => string.Equals(m.Name.Trim(), text, StringComparison.OrdinalIgnoreCase)).ToList();
            return byName.Count switch
            {
                1 => (byName[0].Id, null),
                0 => (null, $"找不到成员「{text}」。"),
                _ => (null, $"有多个成员叫「{text}」，请改填邮箱。"),
            };
        }

        public sealed record Member(Guid Id, string Email, string Name);
    }

    private sealed class ImportOutcome(bool dryRun, int total)
    {
        private readonly List<CrmImportRowError> _errors = [];

        public int Created { get; set; }

        public int Updated { get; set; }

        public int Skipped { get; set; }

        public int Failed { get; private set; }

        public int CompaniesCreated { get; set; }

        public void Fail(ImportRow row, IEnumerable<string> messages)
        {
            Failed++;
            if (_errors.Count < CrmImportLimits.MaxReportedErrors)
            {
                _errors.Add(new CrmImportRowError(row.Number, string.Join(" ", messages)));
            }
        }

        public CrmImportResultDto ToDto() => new(dryRun, total, Created, Updated, Skipped, Failed, CompaniesCreated, _errors);
    }
}

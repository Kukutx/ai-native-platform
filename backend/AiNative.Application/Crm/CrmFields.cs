using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Application.Modules;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Crm;
using AiNative.Domain.Crm;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Crm;

[RequireFeature(CrmFeatures.View)]
public sealed record ListCrmFieldsQuery(CrmRecordType? RecordType = null) : IQuery<IReadOnlyList<CrmFieldDto>>;

[RequireFeature(CrmFeatures.Configure)]
public sealed record CreateCrmFieldCommand(SaveCrmFieldRequest Request) : ICommand<CrmFieldDto>;

[RequireFeature(CrmFeatures.Configure)]
public sealed record UpdateCrmFieldCommand(Guid Id, SaveCrmFieldRequest Request) : ICommand<CrmFieldDto>;

[RequireFeature(CrmFeatures.Configure)]
public sealed record DeleteCrmFieldCommand(Guid Id) : ICommand<Unit>;

[RequireFeature(CrmFeatures.Configure)]
public sealed record ReorderCrmFieldsCommand(ReorderCrmFieldsRequest Request) : ICommand<IReadOnlyList<CrmFieldDto>>;

/// <summary>
/// 自定义字段的数据清理（删除字段、删除选项时）。按 jsonb 在数据库里直接改，不把记录逐条读出来。
/// 只改值，不动 UpdatedAt：这是配置变化，不算编辑记录，也不让别人手里的编辑版本失效。
/// </summary>
public interface ICrmCustomFieldStore
{
    Task RemoveValuesAsync(CrmRecordType recordType, string key, CancellationToken cancellationToken);

    /// <summary>
    /// 去掉已删除选项的值：单选直接清空，多选去掉对应项（去完为空则清空）。
    /// </summary>
    Task RemoveOptionsAsync(CrmRecordType recordType, string key, bool multiple, IReadOnlyCollection<string> options, CancellationToken cancellationToken);
}

public static partial class CrmFieldLimits
{
    public const int MaxFieldsPerRecordType = 50;
    public const int MaxOptions = 100;
    public const int MaxOptionLength = 100;
    public const int MaxTextLength = 2_000;

    public static bool HasOptions(CrmFieldType type) => type is CrmFieldType.Select or CrmFieldType.MultiSelect;

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    public static partial Regex KeyPattern();
}

public sealed class SaveCrmFieldRequestValidator : AbstractValidator<SaveCrmFieldRequest>
{
    public SaveCrmFieldRequestValidator()
    {
        RuleFor(x => x.RecordType).IsInEnum().WithMessage("记录类型不正确。").OverridePropertyName("recordType");
        RuleFor(x => x.Type).IsInEnum().WithMessage("字段类型不正确。").OverridePropertyName("type");
        RuleFor(x => x.Key).NotEmpty().WithMessage("请填写字段标识。")
            .Must(k => CrmFieldLimits.KeyPattern().IsMatch(k)).WithMessage("字段标识以小写字母开头，只能包含小写字母、数字和下划线，最多 40 个字符。")
            .OverridePropertyName("key");
        RuleFor(x => x.Label).Must(l => !string.IsNullOrWhiteSpace(l)).WithMessage("请填写字段名称。").Text(50, "字段名称").OverridePropertyName("label");
        RuleFor(x => x.Description).Text(500, "说明").OverridePropertyName("description");
        RuleFor(x => x.Required).Equal(false).When(x => x.Type == CrmFieldType.Checkbox).WithMessage("“是否”类字段不能设为必填。").OverridePropertyName("required");
        When(x => CrmFieldLimits.HasOptions(x.Type), () =>
        {
            RuleFor(x => x.Options)
                .Must(o => o.Any(v => !string.IsNullOrWhiteSpace(v))).WithMessage("请至少填写一个选项。")
                .Must(o => o.Count <= CrmFieldLimits.MaxOptions).WithMessage($"选项最多 {CrmFieldLimits.MaxOptions} 个。")
                .Must(o => o.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim().ToUpperInvariant()).Distinct().Count() == o.Count(v => !string.IsNullOrWhiteSpace(v)))
                .WithMessage("选项不能重复。")
                .OverridePropertyName("options");
            RuleForEach(x => x.Options).MaximumLength(CrmFieldLimits.MaxOptionLength).WithMessage($"单个选项最多 {CrmFieldLimits.MaxOptionLength} 个字。").OverridePropertyName("options");
        });
    }
}

public sealed class CreateCrmFieldCommandValidator : AbstractValidator<CreateCrmFieldCommand>
{
    public CreateCrmFieldCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveCrmFieldRequestValidator());
}

public sealed class UpdateCrmFieldCommandValidator : AbstractValidator<UpdateCrmFieldCommand>
{
    public UpdateCrmFieldCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveCrmFieldRequestValidator());
}

public sealed class ReorderCrmFieldsCommandValidator : AbstractValidator<ReorderCrmFieldsCommand>
{
    public ReorderCrmFieldsCommandValidator()
    {
        RuleFor(x => x.Request.RecordType).IsInEnum().WithMessage("记录类型不正确。").OverridePropertyName("recordType");
        RuleFor(x => x.Request.Ids).Must(ids => ids.Distinct().Count() == ids.Count).WithMessage("同一个字段不能出现两次。").OverridePropertyName("ids");
    }
}

internal sealed class CrmFieldHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    ITransactionLock locks,
    ICrmCustomFieldStore store,
    IChangeFeed changes) :
    IRequestHandler<ListCrmFieldsQuery, IReadOnlyList<CrmFieldDto>>,
    IRequestHandler<CreateCrmFieldCommand, CrmFieldDto>,
    IRequestHandler<UpdateCrmFieldCommand, CrmFieldDto>,
    IRequestHandler<DeleteCrmFieldCommand, Unit>,
    IRequestHandler<ReorderCrmFieldsCommand, IReadOnlyList<CrmFieldDto>>
{
    public async Task<IReadOnlyList<CrmFieldDto>> HandleAsync(ListCrmFieldsQuery request, CancellationToken cancellationToken)
    {
        var query = db.CrmFieldDefinitions.AsNoTracking();
        if (request.RecordType is { } recordType)
        {
            query = query.Where(f => f.RecordType == recordType);
        }

        var fields = await query.OrderBy(f => f.RecordType).ThenBy(f => f.Position).ThenBy(f => f.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. fields.Select(f => f.ToDto())];
    }

    public async Task<CrmFieldDto> HandleAsync(CreateCrmFieldCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        await LockAsync(cancellationToken).ConfigureAwait(false);
        var siblings = await db.CrmFieldDefinitions.Where(f => f.RecordType == r.RecordType).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (siblings.Count >= CrmFieldLimits.MaxFieldsPerRecordType)
        {
            throw new ConflictException("crm_field.limit_reached", $"每类记录最多 {CrmFieldLimits.MaxFieldsPerRecordType} 个自定义字段。");
        }

        if (siblings.Any(f => f.Key == r.Key))
        {
            throw new ConflictException("crm_field.key_taken", $"已经有标识为「{r.Key}」的字段，请换一个标识。");
        }

        EnsureLabelAvailable(siblings, r.Label, exceptId: null);
        var field = new CrmFieldDefinition
        {
            RecordType = r.RecordType,
            Key = r.Key,
            Label = r.Label.Trim(),
            Type = r.Type,
            Position = siblings.Count == 0 ? 0 : siblings.Max(f => f.Position) + 1,
        };
        Apply(field, r);
        db.CrmFieldDefinitions.Add(field);
        changes.Publish(CrmRealtime.Field, field.Id, RealtimeChangeKind.Created);
        return field.ToDto();
    }

    /// <summary>
    /// 修改名称、说明、选项和是否必填；标识、类型和记录类型创建后不能改（已有数据按它们存储和校验）。
    /// 删掉的选项会从记录里清除。
    /// </summary>
    public async Task<CrmFieldDto> HandleAsync(UpdateCrmFieldCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        await LockAsync(cancellationToken).ConfigureAwait(false);
        var field = await db.CrmFieldDefinitions.FirstOrDefaultAsync(f => f.Id == request.Id, cancellationToken).ConfigureAwait(false)
                    ?? throw new NotFoundException("crm_field", request.Id);
        var errors = new Dictionary<string, string[]>();
        if (field.RecordType != r.RecordType)
        {
            errors["recordType"] = ["字段所属的记录类型不能修改。"];
        }

        if (field.Key != r.Key)
        {
            errors["key"] = ["字段标识创建后不能修改。"];
        }

        if (field.Type != r.Type)
        {
            errors["type"] = ["字段类型创建后不能修改，可以新建一个字段。"];
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        var siblings = await db.CrmFieldDefinitions.Where(f => f.RecordType == field.RecordType).ToListAsync(cancellationToken).ConfigureAwait(false);
        EnsureLabelAvailable(siblings, r.Label, field.Id);

        // 只改了大小写的选项不算删除：已有的值仍能按不区分大小写匹配上。
        var previous = field.Options;
        field.Label = r.Label.Trim();
        Apply(field, r);
        var removed = previous.Where(o => !field.Options.Contains(o, StringComparer.OrdinalIgnoreCase)).ToList();
        if (removed.Count > 0)
        {
            await store.RemoveOptionsAsync(field.RecordType, field.Key, field.Type == CrmFieldType.MultiSelect, removed, cancellationToken).ConfigureAwait(false);
            changes.Publish(RecordEntity(field.RecordType), tenant.TenantId, RealtimeChangeKind.Updated);
        }

        changes.Publish(CrmRealtime.Field, field.Id, RealtimeChangeKind.Updated);
        return field.ToDto();
    }

    /// <summary>
    /// 删除字段，同时清除所有记录里这个字段的值。
    /// </summary>
    public async Task<Unit> HandleAsync(DeleteCrmFieldCommand request, CancellationToken cancellationToken)
    {
        await LockAsync(cancellationToken).ConfigureAwait(false);
        var field = await db.CrmFieldDefinitions.FirstOrDefaultAsync(f => f.Id == request.Id, cancellationToken).ConfigureAwait(false)
                    ?? throw new NotFoundException("crm_field", request.Id);
        await store.RemoveValuesAsync(field.RecordType, field.Key, cancellationToken).ConfigureAwait(false);
        db.CrmFieldDefinitions.Remove(field);
        changes.Publish(CrmRealtime.Field, field.Id, RealtimeChangeKind.Deleted);
        changes.Publish(RecordEntity(field.RecordType), tenant.TenantId, RealtimeChangeKind.Updated);
        return Unit.Value;
    }

    public async Task<IReadOnlyList<CrmFieldDto>> HandleAsync(ReorderCrmFieldsCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        await LockAsync(cancellationToken).ConfigureAwait(false);
        var fields = await db.CrmFieldDefinitions.Where(f => f.RecordType == r.RecordType).ToDictionaryAsync(f => f.Id, cancellationToken).ConfigureAwait(false);
        if (fields.Count != r.Ids.Count || r.Ids.Any(id => !fields.ContainsKey(id)))
        {
            throw new ValidationFailedException(new Dictionary<string, string[]> { ["ids"] = ["字段列表已经变化，请刷新后再调整顺序。"] });
        }

        for (var position = 0; position < r.Ids.Count; position++)
        {
            fields[r.Ids[position]].Position = position;
        }

        changes.Publish(CrmRealtime.Field, tenant.TenantId, RealtimeChangeKind.Updated);
        return [.. r.Ids.Select(id => fields[id].ToDto())];
    }

    public static string RecordEntity(CrmRecordType recordType) => recordType switch
    {
        CrmRecordType.Company => CrmRealtime.Company,
        CrmRecordType.Contact => CrmRealtime.Contact,
        _ => CrmRealtime.Deal,
    };

    // 同一空间的字段配置串行修改：数量上限、标识和名称唯一都基于最新的列表判断。
    private Task LockAsync(CancellationToken cancellationToken) => locks.AcquireAsync($"crm-fields:{tenant.TenantId}", cancellationToken);

    private static void EnsureLabelAvailable(IEnumerable<CrmFieldDefinition> siblings, string label, Guid? exceptId)
    {
        var trimmed = label.Trim();
        if (siblings.Any(f => f.Id != exceptId && string.Equals(f.Label, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ConflictException("crm_field.label_taken", $"已经有名为「{trimmed}」的字段。");
        }
    }

    private static void Apply(CrmFieldDefinition field, SaveCrmFieldRequest r)
    {
        field.Description = r.Description.Trim();
        field.Required = r.Required && r.Type != CrmFieldType.Checkbox;
        field.Options = CrmFieldLimits.HasOptions(r.Type)
            ? [.. r.Options.Select(o => o.Trim()).Where(o => o.Length > 0)]
            : [];
    }
}

/// <summary>
/// 自定义字段的取值规范：界面、接口、智能体和导入提交的值都先经过这里，存进数据库的是统一的形式。
/// 文本为字符串，数字为 JSON 数字，日期为 yyyy-MM-dd 字符串，单选为选项原文，多选为选项数组，是否只存 true。
/// 空值（null、空字符串、空数组、否）不存。
/// </summary>
public static class CrmFieldValues
{
    private static readonly string[] DateFormats = ["yyyy-M-d", "yyyy/M/d", "yyyy.M.d", "yyyyMMdd", "yyyy年M月d日"];

    private static readonly HashSet<string> TrueWords = new(StringComparer.OrdinalIgnoreCase) { "true", "1", "yes", "y", "是", "√", "✓", "✔", "对" };

    private static readonly HashSet<string> FalseWords = new(StringComparer.OrdinalIgnoreCase) { "false", "0", "no", "n", "否", "×", "✗", "不是" };

    /// <param name="normalized">规范后的值；为 null 表示空值，不存这个字段。</param>
    public static bool TryNormalize(CrmFieldDefinition field, JsonElement value, out JsonElement? normalized, [NotNullWhen(false)] out string? error)
    {
        normalized = null;
        error = null;
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        switch (field.Type)
        {
            case CrmFieldType.Text:
                if (Scalar(value) is not { } text)
                {
                    error = $"{field.Label}需要填写文字。";
                    return false;
                }

                if (text.Length > CrmFieldLimits.MaxTextLength)
                {
                    error = $"{field.Label}最多 {CrmFieldLimits.MaxTextLength} 个字。";
                    return false;
                }

                normalized = text.Length == 0 ? null : JsonSerializer.SerializeToElement(text, PlatformJson.Options);
                return true;

            case CrmFieldType.Number:
                if (value.ValueKind == JsonValueKind.Number)
                {
                    if (!value.TryGetDecimal(out var exact))
                    {
                        error = $"{field.Label}超出了数字的范围。";
                        return false;
                    }

                    normalized = JsonSerializer.SerializeToElement(exact, PlatformJson.Options);
                    return true;
                }

                var raw = value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : null;
                if (raw?.Length == 0)
                {
                    return true;
                }

                var cleaned = raw?.Replace(",", string.Empty, StringComparison.Ordinal).Replace("，", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
                if (cleaned is null || !decimal.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    error = $"{field.Label}需要填写数字。";
                    return false;
                }

                normalized = JsonSerializer.SerializeToElement(number, PlatformJson.Options);
                return true;

            case CrmFieldType.Date:
                var dateText = value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : null;
                if (dateText?.Length == 0)
                {
                    return true;
                }

                if (dateText is null || ParseDate(dateText) is not { } date)
                {
                    error = $"{field.Label}需要填写日期，例如 2026-10-12。";
                    return false;
                }

                normalized = JsonSerializer.SerializeToElement(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), PlatformJson.Options);
                return true;

            case CrmFieldType.Select:
                if (Scalar(value) is not { } choice)
                {
                    error = $"{field.Label}只能选一个选项。";
                    return false;
                }

                if (choice.Length == 0)
                {
                    return true;
                }

                if (Option(field, choice) is not { } option)
                {
                    error = $"{field.Label}没有「{choice}」这个选项。";
                    return false;
                }

                normalized = JsonSerializer.SerializeToElement(option, PlatformJson.Options);
                return true;

            case CrmFieldType.MultiSelect:
                List<string> items;
                if (value.ValueKind == JsonValueKind.Array)
                {
                    items = [];
                    foreach (var item in value.EnumerateArray())
                    {
                        if (Scalar(item) is not { } itemText)
                        {
                            error = $"{field.Label}的选项需要是文字。";
                            return false;
                        }

                        if (itemText.Length > 0)
                        {
                            items.Add(itemText);
                        }
                    }
                }
                else if (Scalar(value) is { } joined)
                {
                    items = [.. CrmRecords.SplitList(joined)];
                }
                else
                {
                    error = $"{field.Label}需要选择选项。";
                    return false;
                }

                var options = new List<string>();
                foreach (var item in items)
                {
                    if (Option(field, item) is not { } matched)
                    {
                        error = $"{field.Label}没有「{item}」这个选项。";
                        return false;
                    }

                    if (!options.Contains(matched, StringComparer.Ordinal))
                    {
                        options.Add(matched);
                    }
                }

                normalized = options.Count == 0 ? null : JsonSerializer.SerializeToElement(options, PlatformJson.Options);
                return true;

            case CrmFieldType.Checkbox:
                bool? flag = value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number when value.TryGetDecimal(out var n) && n is 0 or 1 => n == 1,
                    JsonValueKind.String when value.GetString()!.Trim() is var word && (word.Length == 0 || FalseWords.Contains(word)) => false,
                    JsonValueKind.String when TrueWords.Contains(value.GetString()!.Trim()) => true,
                    _ => null,
                };
                if (flag is null)
                {
                    error = $"{field.Label}需要填写“是”或“否”。";
                    return false;
                }

                normalized = flag.Value ? JsonSerializer.SerializeToElement(true, PlatformJson.Options) : null;
                return true;

            default:
                error = $"{field.Label}的类型不受支持。";
                return false;
        }
    }

    /// <summary>
    /// 在已有值的基础上应用修改（<paramref name="current"/> 为空即整体替换），再检查必填。
    /// 修改里出现未定义的字段、值不合法、缺了必填字段时，把错误按字段标识写进 <paramref name="errors"/>。
    /// 已删除字段的残留值不会带出来。
    /// </summary>
    public static Dictionary<string, JsonElement> Merge(
        IReadOnlyList<CrmFieldDefinition> fields,
        IReadOnlyDictionary<string, JsonElement>? current,
        IReadOnlyDictionary<string, JsonElement> changes,
        IDictionary<string, string> errors)
    {
        var byKey = fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in current ?? new Dictionary<string, JsonElement>())
        {
            if (byKey.ContainsKey(key))
            {
                result[key] = value;
            }
        }

        foreach (var (key, value) in changes)
        {
            if (!byKey.TryGetValue(key, out var field))
            {
                errors[key] = $"没有标识为「{key}」的自定义字段。";
                continue;
            }

            if (!TryNormalize(field, value, out var normalized, out var error))
            {
                errors[key] = error;
                continue;
            }

            if (normalized is { } stored)
            {
                result[key] = stored;
            }
            else
            {
                result.Remove(key);
            }
        }

        foreach (var field in fields.Where(f => f.Required && !result.ContainsKey(f.Key) && !errors.ContainsKey(f.Key)))
        {
            errors[field.Key] = $"请填写{field.Label}。";
        }

        return result;
    }

    /// <summary>
    /// 把值转成给人看的文字（导入预览、提示里用）。
    /// </summary>
    public static string Display(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => string.Join("、", value.EnumerateArray().Select(Display)),
        JsonValueKind.True => "是",
        JsonValueKind.False => "否",
        JsonValueKind.String => value.GetString()!,
        _ => value.GetRawText(),
    };

    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!.Trim(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    private static string? Option(CrmFieldDefinition field, string text)
        => field.Options.FirstOrDefault(o => string.Equals(o, text, StringComparison.OrdinalIgnoreCase));

    private static DateOnly? ParseDate(string text)
    {
        if (DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        // 带时间的写法（2026-10-12T08:00:00Z、2026-10-12 08:00）取日期部分，不做时区换算。
        return text.Length > 10 && text[10] is 'T' or ' '
               && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var prefix)
            ? prefix
            : null;
    }
}

/// <summary>
/// 一次请求内复用的自定义字段定义，以及保存记录时的取值校验。
/// </summary>
internal sealed class CrmCustomFields(IAppDbContext db)
{
    private readonly Dictionary<CrmRecordType, IReadOnlyList<CrmFieldDefinition>> _definitions = [];

    public async Task<IReadOnlyList<CrmFieldDefinition>> DefinitionsAsync(CrmRecordType recordType, CancellationToken cancellationToken)
    {
        if (!_definitions.TryGetValue(recordType, out var fields))
        {
            fields = await db.CrmFieldDefinitions.AsNoTracking().Where(f => f.RecordType == recordType)
                .OrderBy(f => f.Position).ThenBy(f => f.CreatedAt)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            _definitions[recordType] = fields;
        }

        return fields;
    }

    /// <summary>
    /// 新建记录：没传也要检查必填字段。
    /// </summary>
    public async Task<Dictionary<string, JsonElement>> ForCreateAsync(CrmRecordType recordType, IReadOnlyDictionary<string, JsonElement>? values, CancellationToken cancellationToken)
        => Validated(Merge(await DefinitionsAsync(recordType, cancellationToken).ConfigureAwait(false), null, values ?? new Dictionary<string, JsonElement>()));

    /// <summary>
    /// 编辑记录：没传表示不修改（返回 null）；传了就整体替换。
    /// </summary>
    public async Task<Dictionary<string, JsonElement>?> ForUpdateAsync(CrmRecordType recordType, IReadOnlyDictionary<string, JsonElement>? values, CancellationToken cancellationToken)
        => values is null ? null : Validated(Merge(await DefinitionsAsync(recordType, cancellationToken).ConfigureAwait(false), null, values));

    private static (Dictionary<string, JsonElement> Values, Dictionary<string, string> Errors) Merge(
        IReadOnlyList<CrmFieldDefinition> fields,
        IReadOnlyDictionary<string, JsonElement>? current,
        IReadOnlyDictionary<string, JsonElement> changes)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        return (CrmFieldValues.Merge(fields, current, changes, errors), errors);
    }

    private static Dictionary<string, JsonElement> Validated((Dictionary<string, JsonElement> Values, Dictionary<string, string> Errors) result)
        => result.Errors.Count == 0
            ? result.Values
            : throw new ValidationFailedException(result.Errors.ToDictionary(e => $"customFields.{e.Key}", e => new[] { e.Value }));
}

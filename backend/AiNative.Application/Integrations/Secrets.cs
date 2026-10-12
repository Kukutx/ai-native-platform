using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Integrations;
using AiNative.Domain.Integrations;
using AiNative.Domain.Workflows;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Integrations;

/// <summary>
/// 密钥加解密。密文和空间、名称绑定：挪到别的空间或换个名字都解不开。
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(Guid tenantId, string name, string value);

    /// <exception cref="SecretUnavailableException">解不开（主密钥换过且没保留旧的，或密文被篡改）。</exception>
    string Unprotect(Guid tenantId, string name, byte[] cipherText);
}

/// <summary>
/// 按名称读取当前空间的密钥明文，只在真正发请求前调用。同一作用域内缓存。
/// </summary>
public interface ITenantSecrets
{
    /// <summary>
    /// 这些密钥的明文；不存在的名称不在结果里。
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken);

    /// <summary>
    /// 这些名称里已经存在的（不解密）。
    /// </summary>
    Task<IReadOnlySet<string>> ExistingAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken);
}

public sealed class SecretUnavailableException(string message) : AppException("integrations.secret_unavailable", message);

/// <summary>
/// 请求头里的密钥引用 {{ secrets.名称 }}。引用必须单独成段，不能放进表达式里运算；
/// 渲染时先切出引用，其余部分照常渲染后再拼回明文，所以触发数据里混进的 "{{ secrets.X }}" 不会被当成引用。
/// </summary>
public static partial class SecretTemplate
{
    public const string RootName = "secrets";

    public const int MaxNameLength = 64;

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,63}$")]
    public static partial Regex NamePattern();

    [GeneratedRegex(@"\{\{\s*secrets\.([A-Z][A-Z0-9_]{0,63})\s*\}\}")]
    private static partial Regex ReferencePattern();

    public static IEnumerable<string> References(string? text)
        => string.IsNullOrEmpty(text) ? [] : ReferencePattern().Matches(text).Select(m => m.Groups[1].Value);

    public static bool HasReferences(string? text) => !string.IsNullOrEmpty(text) && ReferencePattern().IsMatch(text);

    /// <summary>
    /// 去掉引用后剩下的文字，用来校验其余部分。
    /// </summary>
    public static string Strip(string text) => ReferencePattern().Replace(text, " ");

    /// <summary>
    /// 引用换成明文，其余部分交给 <paramref name="renderRest"/>（工作流模板渲染，或原样保留）。
    /// </summary>
    public static string Render(string text, IReadOnlyDictionary<string, string> values, Func<string, string> renderRest)
    {
        var matches = ReferencePattern().Matches(text);
        if (matches.Count == 0)
        {
            return renderRest(text);
        }

        var builder = new StringBuilder(text.Length);
        var position = 0;
        foreach (Match match in matches)
        {
            if (match.Index > position)
            {
                builder.Append(renderRest(text[position..match.Index]));
            }

            var name = match.Groups[1].Value;
            builder.Append(values.TryGetValue(name, out var value) ? value : throw new SecretUnavailableException($"密钥「{name}」不存在。"));
            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            builder.Append(renderRest(text[position..]));
        }

        return builder.ToString();
    }

    /// <summary>
    /// 渲染一组请求头里的密钥引用；只有用到密钥时才去读库解密。
    /// </summary>
    public static async Task<Dictionary<string, string>> RenderHeadersAsync(
        IEnumerable<KeyValuePair<string, string>> headers,
        ITenantSecrets secrets,
        Func<string, string> renderRest,
        CancellationToken cancellationToken)
    {
        var list = headers.ToList();
        var names = list.SelectMany(h => References(h.Value)).Distinct(StringComparer.Ordinal).ToList();
        var values = names.Count == 0
            ? new Dictionary<string, string>()
            : await secrets.GetAsync(names, cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in list)
        {
            result[name] = Render(value, values, renderRest);
        }

        return result;
    }
}

/// <summary>
/// 从外部返回的内容里抹掉本次发出去的密钥明文。对方可能把请求头原样回显（调试接口、部分错误响应），
/// 这些内容会写进运行记录、交给模型，能看到它们的人不一定有权看密钥。太短的值不处理，免得误伤正常文字。
/// </summary>
public sealed class SecretRedactor
{
    public const string Mask = "***";

    public const int MinLength = 4;

    public static readonly SecretRedactor None = new([]);

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string[] _values;

    public SecretRedactor(IEnumerable<string> values)
        => _values = [.. values.Where(v => v.Length >= MinLength).Distinct(StringComparer.Ordinal).OrderByDescending(v => v.Length)];

    public string Redact(string text)
    {
        foreach (var value in _values)
        {
            if (text.Contains(value, StringComparison.Ordinal))
            {
                text = text.Replace(value, Mask, StringComparison.Ordinal);
            }
        }

        return text;
    }

    public string? RedactNullable(string? text) => text is null ? null : Redact(text);

    /// <summary>
    /// 抹掉 JSON 里字符串值和属性名中的密钥；没有命中时原样返回。
    /// </summary>
    public JsonElement Redact(JsonElement element)
    {
        if (_values.Length == 0 || !Contains(element))
        {
            return element;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            Write(writer, element);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private bool Contains(string text) => _values.Any(v => text.Contains(v, StringComparison.Ordinal));

    private bool Contains(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => Contains(element.GetString()!),
        JsonValueKind.Object => element.EnumerateObject().Any(p => Contains(p.Name) || Contains(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(Contains),
        _ => false,
    };

    private void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(Redact(property.Name));
                    Write(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(Redact(element.GetString()!));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

/// <summary>
/// 保存引用密钥的配置与删除密钥互斥：两边都在事务里先拿同一把锁再检查，
/// 删除不会漏掉刚保存的引用，保存也不会引用刚被删掉的密钥。
/// </summary>
internal sealed class SecretReferences(ITenantSecrets secrets, ITransactionLock locks, ITenantContext tenant)
{
    public static string LockKey(Guid tenantId) => $"tenant-secrets:{tenantId}";

    /// <summary>
    /// 必须在事务里调用，锁到事务结束。
    /// </summary>
    public Task LockAsync(CancellationToken cancellationToken) => locks.AcquireAsync(LockKey(tenant.TenantId), cancellationToken);

    /// <summary>
    /// 加锁后返回这些名称里不存在的；没有引用时不加锁。必须在事务里调用。
    /// </summary>
    public async Task<IReadOnlyList<string>> LockAndFindMissingAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken)
    {
        if (names.Count == 0)
        {
            return [];
        }

        await LockAsync(cancellationToken).ConfigureAwait(false);
        var existing = await secrets.ExistingAsync(names, cancellationToken).ConfigureAwait(false);
        return [.. names.Where(n => !existing.Contains(n)).Distinct(StringComparer.Ordinal)];
    }
}

internal sealed class TenantSecrets(IAppDbContext db, ITenantContext tenant, ISecretProtector protector) : ITenantSecrets
{
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);

    public async Task<IReadOnlyDictionary<string, string>> GetAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken)
    {
        var missing = names.Where(n => !_cache.ContainsKey(n)).Distinct(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            var rows = await db.TenantSecrets.AsNoTracking().Where(s => missing.Contains(s.Name))
                .Select(s => new { s.Name, s.CipherText })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                _cache[row.Name] = protector.Unprotect(tenant.TenantId, row.Name, row.CipherText);
            }
        }

        return names.Where(_cache.ContainsKey).Distinct(StringComparer.Ordinal).ToDictionary(n => n, n => _cache[n], StringComparer.Ordinal);
    }

    public async Task<IReadOnlySet<string>> ExistingAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken)
    {
        if (names.Count == 0)
        {
            return new HashSet<string>();
        }

        var list = names.Distinct(StringComparer.Ordinal).ToList();
        var existing = await db.TenantSecrets.AsNoTracking().Where(s => list.Contains(s.Name)).Select(s => s.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return existing.ToHashSet(StringComparer.Ordinal);
    }
}

[RequireFeature(IntegrationFeatures.View)]
public sealed record ListTenantSecretsQuery : IQuery<IReadOnlyList<TenantSecretDto>>;

[RequireFeature(IntegrationFeatures.Manage)]
public sealed record CreateTenantSecretCommand(CreateTenantSecretRequest Request) : ICommand<TenantSecretDto>;

[RequireFeature(IntegrationFeatures.Manage)]
public sealed record UpdateTenantSecretCommand(Guid Id, UpdateTenantSecretRequest Request) : ICommand<TenantSecretDto>;

[RequireFeature(IntegrationFeatures.Manage)]
public sealed record DeleteTenantSecretCommand(Guid Id) : ICommand<Unit>;

public sealed class CreateTenantSecretCommandValidator : AbstractValidator<CreateTenantSecretCommand>
{
    public const int MaxValueLength = 8_000;

    public CreateTenantSecretCommandValidator()
    {
        RuleFor(x => x.Request.Name).NotEmpty().WithMessage("请填写名称。")
            .Matches(SecretTemplate.NamePattern()).WithMessage("名称以大写字母开头，只能包含大写字母、数字和下划线，最长 64 个字符，例如 GITHUB_TOKEN。")
            .OverridePropertyName("name");
        RuleFor(x => x.Request.Value).NotEmpty().WithMessage("请填写密钥的值。")
            .MaximumLength(MaxValueLength).WithMessage($"值不能超过 {MaxValueLength} 个字符。")
            .OverridePropertyName("value");
        RuleFor(x => x.Request.Description).Text(500, "说明").OverridePropertyName("description");
    }
}

public sealed class UpdateTenantSecretCommandValidator : AbstractValidator<UpdateTenantSecretCommand>
{
    public UpdateTenantSecretCommandValidator()
    {
        RuleFor(x => x.Request.Value!).NotEmpty().WithMessage("密钥的值不能为空；只改说明时不要传值。")
            .MaximumLength(CreateTenantSecretCommandValidator.MaxValueLength).WithMessage($"值不能超过 {CreateTenantSecretCommandValidator.MaxValueLength} 个字符。")
            .When(x => x.Request.Value is not null)
            .OverridePropertyName("value");
        RuleFor(x => x.Request.Description).Text(500, "说明").OverridePropertyName("description");
    }
}

internal sealed class TenantSecretHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    ISecretProtector protector,
    SecretReferences references,
    IChangeFeed changes,
    TimeProvider clock) :
    IRequestHandler<ListTenantSecretsQuery, IReadOnlyList<TenantSecretDto>>,
    IRequestHandler<CreateTenantSecretCommand, TenantSecretDto>,
    IRequestHandler<UpdateTenantSecretCommand, TenantSecretDto>,
    IRequestHandler<DeleteTenantSecretCommand, Unit>
{
    public async Task<IReadOnlyList<TenantSecretDto>> HandleAsync(ListTenantSecretsQuery request, CancellationToken cancellationToken)
    {
        var secrets = await db.TenantSecrets.AsNoTracking().OrderBy(s => s.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        return await ToDtosAsync(secrets, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TenantSecretDto> HandleAsync(CreateTenantSecretCommand request, CancellationToken cancellationToken)
    {
        var name = request.Request.Name.Trim();
        if (await db.TenantSecrets.AnyAsync(s => s.Name == name, cancellationToken).ConfigureAwait(false))
        {
            throw new ConflictException("tenant_secret.duplicate_name", $"已经有名为 {name} 的密钥了，要换值请编辑它。");
        }

        var secret = new TenantSecret
        {
            Name = name,
            Description = request.Request.Description.Trim(),
            CipherText = protector.Protect(tenant.TenantId, name, request.Request.Value),
            CreatedBy = tenant.Actor,
            UpdatedBy = tenant.Actor,
        };
        db.TenantSecrets.Add(secret);
        changes.Publish(IntegrationRealtime.Secret, secret.Id, RealtimeChangeKind.Created);
        return (await ToDtosAsync([secret], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<TenantSecretDto> HandleAsync(UpdateTenantSecretCommand request, CancellationToken cancellationToken)
    {
        var secret = await db.TenantSecrets.FirstOrDefaultAsync(s => s.Id == request.Id, cancellationToken).ConfigureAwait(false)
                     ?? throw new NotFoundException("tenant_secret", request.Id, "密钥不存在。");
        if (request.Request.Value is { } value)
        {
            secret.CipherText = protector.Protect(tenant.TenantId, secret.Name, value);
        }

        secret.Description = request.Request.Description.Trim();
        secret.UpdatedBy = tenant.Actor;
        secret.UpdatedAt = clock.GetUtcNow();
        changes.Publish(IntegrationRealtime.Secret, secret.Id, RealtimeChangeKind.Updated);
        return (await ToDtosAsync([secret], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>
    /// 还被外部服务或工作流引用的密钥不能删，免得它们悄悄开始失败。和保存引用的操作互斥，检查之后不会冒出新引用。
    /// </summary>
    public async Task<Unit> HandleAsync(DeleteTenantSecretCommand request, CancellationToken cancellationToken)
    {
        await references.LockAsync(cancellationToken).ConfigureAwait(false);
        var secret = await db.TenantSecrets.FirstOrDefaultAsync(s => s.Id == request.Id, cancellationToken).ConfigureAwait(false)
                     ?? throw new NotFoundException("tenant_secret", request.Id, "密钥不存在。");
        var users = await FindUsersAsync(secret.Name, cancellationToken).ConfigureAwait(false);
        if (users.Count > 0)
        {
            throw new ConflictException(
                "tenant_secret.in_use",
                $"密钥 {secret.Name} 正被{string.Join("、", users.Take(5))}{(users.Count > 5 ? $" 等 {users.Count} 处" : string.Empty)}使用，先改掉这些引用再删除。");
        }

        db.TenantSecrets.Remove(secret);
        changes.Publish(IntegrationRealtime.Secret, secret.Id, RealtimeChangeKind.Deleted);
        return Unit.Value;
    }

    private async Task<List<string>> FindUsersAsync(string name, CancellationToken cancellationToken)
    {
        var users = new List<string>();
        var servers = await db.McpServers.AsNoTracking().Select(s => new { s.Name, s.Headers }).ToListAsync(cancellationToken).ConfigureAwait(false);
        users.AddRange(servers.Where(s => s.Headers.Any(h => SecretTemplate.References(h.Value).Contains(name))).Select(s => $"外部服务「{s.Name}」"));

        var workflows = await db.Workflows.AsNoTracking().Select(w => new { w.Name, w.Steps }).ToListAsync(cancellationToken).ConfigureAwait(false);
        users.AddRange(workflows
            .Where(w => w.Steps.SelectMany(s => s.SelfAndDescendants())
                .Any(s => s.Kind == WorkflowStepKind.Http && (s.Headers ?? []).Values.Any(v => SecretTemplate.References(v).Contains(name))))
            .Select(w => $"工作流「{w.Name}」"));
        return users;
    }

    private async Task<IReadOnlyList<TenantSecretDto>> ToDtosAsync(IReadOnlyList<TenantSecret> secrets, CancellationToken cancellationToken)
    {
        var names = await ActorNames.ResolveAsync(db, secrets.SelectMany(s => new[] { s.CreatedBy, s.UpdatedBy }), cancellationToken).ConfigureAwait(false);
        return [.. secrets.Select(s => new TenantSecretDto(s.Id, s.Name, s.Description, names(s.CreatedBy), names(s.UpdatedBy), s.CreatedAt, s.UpdatedAt))];
    }
}

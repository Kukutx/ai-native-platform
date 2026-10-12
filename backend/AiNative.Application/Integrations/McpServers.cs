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
using Microsoft.Extensions.Logging;

namespace AiNative.Application.Integrations;

[RequireFeature(IntegrationFeatures.View)]
public sealed record ListMcpServersQuery : IQuery<IReadOnlyList<McpServerDto>>;

[RequireFeature(IntegrationFeatures.View)]
public sealed record GetMcpServerQuery(Guid Id) : IQuery<McpServerDto>;

/// <summary>
/// 不走统一事务：先连外部服务读工具列表（可能要几秒），再单独提交，不让数据库事务等网络。
/// 连不上也照样保存，错误记在 lastError 里，改好后刷新即可。
/// </summary>
[RequireFeature(IntegrationFeatures.Manage)]
public sealed record CreateMcpServerCommand(SaveMcpServerRequest Request) : IRequest<McpServerDto>;

[RequireFeature(IntegrationFeatures.Manage)]
public sealed record UpdateMcpServerCommand(Guid Id, SaveMcpServerRequest Request) : IRequest<McpServerDto>;

/// <summary>
/// 重新读取工具列表。
/// </summary>
[RequireFeature(IntegrationFeatures.Manage)]
public sealed record RefreshMcpServerToolsCommand(Guid Id) : IRequest<McpServerDto>;

/// <summary>
/// 调整一个工具（开关、确认只读）。只改给出的项，几个人同时调整不同工具互不覆盖。
/// </summary>
[RequireFeature(IntegrationFeatures.Manage)]
public sealed record UpdateMcpToolCommand(Guid Id, UpdateMcpToolRequest Request) : ICommand<McpServerDto>;

[RequireFeature(IntegrationFeatures.Manage)]
public sealed record DeleteMcpServerCommand(Guid Id) : ICommand<Unit>;

public sealed partial class SaveMcpServerRequestValidator : AbstractValidator<SaveMcpServerRequest>
{
    public const int MaxHeaders = 20;

    public SaveMcpServerRequestValidator()
    {
        RuleFor(x => x.Key).NotEmpty().WithMessage("请填写标识。")
            .Matches(McpToolNames.ServerKeyPattern()).WithMessage("标识为 2 到 20 个小写字母或数字，以字母开头，不能有下划线，例如 github。")
            .OverridePropertyName("key");
        RuleFor(x => x.Name).NotEmpty().WithMessage("请填写名称。").Text(100, "名称").OverridePropertyName("name");
        RuleFor(x => x.Description).Text(1_000, "说明").OverridePropertyName("description");
        RuleFor(x => x.Url).NotEmpty().WithMessage("请填写服务地址。").MaximumLength(2_000).WithMessage("地址太长。")
            .Must(IsHttpUrl).WithMessage("请填写以 http:// 或 https:// 开头的完整地址，例如 https://example.com/mcp。")
            .Must(HasNoCredentials).WithMessage("地址里不能包含用户名和密码，请把令牌存进密钥，再在请求头里引用。")
            .OverridePropertyName("url");
        RuleFor(x => x.ApprovalMode).IsInEnum().OverridePropertyName("approvalMode");
        RuleFor(x => x.Headers).Must(h => h.Count <= MaxHeaders).WithMessage($"请求头不能超过 {MaxHeaders} 个。")
            .Must(h => h.Select(x => x.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == h.Count).WithMessage("请求头名称不能重复。")
            .OverridePropertyName("headers");
        RuleForEach(x => x.Headers).ChildRules(header =>
        {
            header.RuleFor(h => h.Name).Must(n => HeaderNamePattern().IsMatch(n?.Trim() ?? string.Empty)).WithMessage(h => $"请求头名称 {h.Name} 不合法。");
            header.RuleFor(h => h.Value).NotNull().MaximumLength(2_000).WithMessage("请求头的值不能超过 2000 个字符。")
                .Must(v => !SecretTemplate.Strip(v ?? string.Empty).Contains("{{", StringComparison.Ordinal))
                .WithMessage("请求头里只能用 {{ secrets.名称 }} 引用密钥，名称为大写字母、数字和下划线。");
        }).OverridePropertyName("headers");
    }

    private static bool IsHttpUrl(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// 地址对能查看集成的人都可见，不能带凭据。
    /// </summary>
    private static bool HasNoCredentials(string? url)
        => !Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo);

    [GeneratedRegex("^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,100}$")]
    private static partial Regex HeaderNamePattern();
}

public sealed class CreateMcpServerCommandValidator : AbstractValidator<CreateMcpServerCommand>
{
    public CreateMcpServerCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveMcpServerRequestValidator()).OverridePropertyName(string.Empty);
}

public sealed class UpdateMcpServerCommandValidator : AbstractValidator<UpdateMcpServerCommand>
{
    public UpdateMcpServerCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveMcpServerRequestValidator()).OverridePropertyName(string.Empty);
}

public sealed class UpdateMcpToolCommandValidator : AbstractValidator<UpdateMcpToolCommand>
{
    public UpdateMcpToolCommandValidator()
        => RuleFor(x => x.Request.Tool).NotEmpty().WithMessage("请指定工具。").OverridePropertyName("tool");
}

/// <summary>
/// 读取工具列表的结果。<see cref="Tools"/> 为 null 表示没读到，原因在 <see cref="Error"/> 里。
/// </summary>
internal sealed record McpDiscovery(IReadOnlyList<McpRemoteTool>? Tools, string? Error);

/// <summary>
/// 连接外部服务：渲染请求头里的密钥、读取工具列表，并把读到的工具合并进保存的快照。
/// </summary>
internal sealed class McpServerTools(IMcpGateway gateway, ITenantSecrets secrets, ILogger<McpServerTools> logger)
{
    public const int MaxTools = 200;

    /// <summary>
    /// 记住的已关闭工具数量上限（包括暂时从服务里消失的）。
    /// </summary>
    public const int MaxRememberedTools = 1_000;

    private const int MaxTitleLength = 200;

    private const int MaxDescriptionLength = 4_000;

    private static readonly JsonElement EmptyObjectSchema = PlatformJson.Parse("""{"type":"object","properties":{}}""");

    public static IReadOnlyList<string> SecretNames(IEnumerable<McpServerHeader> headers)
        => [.. headers.SelectMany(h => SecretTemplate.References(h.Value)).Distinct(StringComparer.Ordinal)];

    public async Task<McpEndpoint> EndpointAsync(McpServer server, CancellationToken cancellationToken)
    {
        var headers = await SecretTemplate.RenderHeadersAsync(
            server.Headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)), secrets, text => text, cancellationToken).ConfigureAwait(false);
        return new McpEndpoint(server.Id, new Uri(server.Url), headers);
    }

    /// <summary>
    /// 请求头里用到的密钥明文，用来从外部返回的内容里抹掉它们。同一作用域内读过的密钥不再查库。
    /// </summary>
    public async Task<SecretRedactor> RedactorAsync(McpServer server, CancellationToken cancellationToken)
    {
        var names = SecretNames(server.Headers);
        return names.Count == 0
            ? SecretRedactor.None
            : new SecretRedactor((await secrets.GetAsync(names, cancellationToken).ConfigureAwait(false)).Values);
    }

    /// <summary>
    /// 读取工具列表，最多 <see cref="MaxTools"/> 个。失败时返回错误说明，不抛出。
    /// </summary>
    public async Task<McpDiscovery> DiscoverAsync(McpServer server, CancellationToken cancellationToken)
    {
        var redactor = SecretRedactor.None;
        try
        {
            var endpoint = await EndpointAsync(server, cancellationToken).ConfigureAwait(false);
            redactor = await RedactorAsync(server, cancellationToken).ConfigureAwait(false);
            var list = await gateway.ListToolsAsync(endpoint, MaxTools, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<McpRemoteTool> tools = [.. list.Tools.Take(MaxTools).Select(t => Clean(t, redactor))];
            return new McpDiscovery(tools, list.HasMore ? $"服务提供的工具超过 {MaxTools} 个，只接入了前 {MaxTools} 个。" : null);
        }
        catch (Exception ex) when (ex is McpGatewayException or SecretUnavailableException)
        {
            var message = redactor.Redact(ex.Message);
            logger.LogInformation("MCP server {ServerId} discovery failed: {Message}", server.Id, message);
            return new McpDiscovery(null, message);
        }
    }

    /// <summary>
    /// 把读到的工具写进服务。已有工具沿用原来的标识；关掉的工具即使暂时消失也记着，
    /// 确认只读的只保留仍然存在的。要在加锁后对最新的记录调用，不覆盖期间别人做的调整。
    /// </summary>
    public static void Apply(McpServer server, McpDiscovery discovery, DateTimeOffset now)
    {
        if (discovery.Tools is { } remote)
        {
            var previous = server.Tools.DistinctBy(t => t.Name, StringComparer.Ordinal).ToDictionary(t => t.Name, t => t.Slug, StringComparer.Ordinal);
            var slugs = McpToolNames.AssignSlugs(remote.Select(t => t.Name), previous);
            server.Tools = [.. remote.Select(t => new McpToolSnapshot
            {
                Name = t.Name,
                Slug = slugs[t.Name],
                Title = t.Title,
                Description = t.Description ?? string.Empty,
                InputSchema = t.InputSchema,
                ReadOnlyHint = t.ReadOnlyHint,
            })];
            var names = slugs.Keys.ToHashSet(StringComparer.Ordinal);
            server.ReadOnlyTools = [.. server.ReadOnlyTools.Where(names.Contains)];
            server.DisabledTools = [.. server.DisabledTools.OrderByDescending(names.Contains).Take(MaxRememberedTools)];
            server.ToolsRefreshedAt = now;
        }

        server.LastError = discovery.Error;
    }

    private static McpRemoteTool Clean(McpRemoteTool tool, SecretRedactor redactor) => tool with
    {
        Title = string.IsNullOrWhiteSpace(tool.Title) ? null : Truncate(redactor.Redact(tool.Title.Trim()), MaxTitleLength),
        Description = Truncate(redactor.Redact(tool.Description?.Trim() ?? string.Empty), MaxDescriptionLength),
        InputSchema = tool.InputSchema.ValueKind == JsonValueKind.Object ? redactor.Redact(tool.InputSchema) : EmptyObjectSchema,
    };

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

/// <summary>
/// 新建、修改、刷新都不走统一事务：先连外部服务读工具列表（可能要几秒），再加锁读出最新记录合并保存，
/// 数据库事务不等网络，期间别人对工具的调整也不会被覆盖。
/// </summary>
internal sealed class McpServerHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    ITenantSecrets secrets,
    SecretReferences secretReferences,
    McpServerTools tools,
    IUnitOfWork unitOfWork,
    ITransactionLock locks,
    IChangeFeed changes,
    TimeProvider clock) :
    IRequestHandler<ListMcpServersQuery, IReadOnlyList<McpServerDto>>,
    IRequestHandler<GetMcpServerQuery, McpServerDto>,
    IRequestHandler<CreateMcpServerCommand, McpServerDto>,
    IRequestHandler<UpdateMcpServerCommand, McpServerDto>,
    IRequestHandler<RefreshMcpServerToolsCommand, McpServerDto>,
    IRequestHandler<UpdateMcpToolCommand, McpServerDto>,
    IRequestHandler<DeleteMcpServerCommand, Unit>
{
    public static string LockKey(Guid serverId) => $"mcp-server:{serverId}";

    public async Task<IReadOnlyList<McpServerDto>> HandleAsync(ListMcpServersQuery request, CancellationToken cancellationToken)
    {
        var servers = await db.McpServers.AsNoTracking().OrderBy(s => s.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        return await ToDtosAsync(servers, cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpServerDto> HandleAsync(GetMcpServerQuery request, CancellationToken cancellationToken)
        => await ToDtoAsync(await FindAsync(request.Id, tracked: false, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task<McpServerDto> HandleAsync(CreateMcpServerCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var key = r.Key.Trim();
        await EnsureKeyAvailableAsync(key, null, cancellationToken).ConfigureAwait(false);
        EnsureCanUseSecrets(null, r);
        await EnsureSecretsExistAsync(r.Headers, locked: false, cancellationToken).ConfigureAwait(false);

        var server = new McpServer { Key = key, Name = r.Name.Trim(), Url = r.Url.Trim(), CreatedBy = tenant.Actor };
        Apply(server, r);
        var discovery = await tools.DiscoverAsync(server, cancellationToken).ConfigureAwait(false);
        return await unitOfWork.RunAsync(async () =>
        {
            await EnsureSecretsExistAsync(server.Headers, locked: true, cancellationToken).ConfigureAwait(false);
            McpServerTools.Apply(server, discovery, clock.GetUtcNow());
            db.McpServers.Add(server);
            changes.Publish(IntegrationRealtime.McpServer, server.Id, RealtimeChangeKind.Created);
            return await ToDtoAsync(server, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpServerDto> HandleAsync(UpdateMcpServerCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var draft = await FindAsync(request.Id, tracked: false, cancellationToken).ConfigureAwait(false);
        var key = r.Key.Trim();
        if (key != draft.Key)
        {
            await EnsureKeyAvailableAsync(key, draft.Id, cancellationToken).ConfigureAwait(false);
            await EnsureNotReferencedAsync(draft, "改标识", cancellationToken).ConfigureAwait(false);
        }

        EnsureCanUseSecrets(draft, r);
        await EnsureSecretsExistAsync(r.Headers, locked: false, cancellationToken).ConfigureAwait(false);

        // 用新的地址和请求头读取工具列表（draft 没被跟踪，改它不会保存）。
        draft.Url = r.Url.Trim();
        Apply(draft, r);
        var discovery = await tools.DiscoverAsync(draft, cancellationToken).ConfigureAwait(false);

        return await unitOfWork.RunAsync(async () =>
        {
            await locks.AcquireAsync(LockKey(request.Id), cancellationToken).ConfigureAwait(false);
            var server = await FindAsync(request.Id, tracked: true, cancellationToken).ConfigureAwait(false);
            EnsureCanUseSecrets(server, r);
            await EnsureSecretsExistAsync(r.Headers, locked: true, cancellationToken).ConfigureAwait(false);
            server.Key = key;
            server.Name = r.Name.Trim();
            server.Url = r.Url.Trim();
            Apply(server, r);
            McpServerTools.Apply(server, discovery, clock.GetUtcNow());
            server.UpdatedAt = clock.GetUtcNow();
            changes.Publish(IntegrationRealtime.McpServer, server.Id, RealtimeChangeKind.Updated);
            return await ToDtoAsync(server, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpServerDto> HandleAsync(RefreshMcpServerToolsCommand request, CancellationToken cancellationToken)
    {
        var draft = await FindAsync(request.Id, tracked: false, cancellationToken).ConfigureAwait(false);
        var discovery = await tools.DiscoverAsync(draft, cancellationToken).ConfigureAwait(false);
        return await unitOfWork.RunAsync(async () =>
        {
            await locks.AcquireAsync(LockKey(request.Id), cancellationToken).ConfigureAwait(false);
            var server = await FindAsync(request.Id, tracked: true, cancellationToken).ConfigureAwait(false);

            // 读取期间地址或请求头被改过：这次读到的是旧配置的结果，修改时已经按新配置读过了。
            if (server.Url == draft.Url && server.Headers.SequenceEqual(draft.Headers))
            {
                McpServerTools.Apply(server, discovery, clock.GetUtcNow());
                server.UpdatedAt = clock.GetUtcNow();
                changes.Publish(IntegrationRealtime.McpServer, server.Id, RealtimeChangeKind.Updated);
            }

            return await ToDtoAsync(server, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpServerDto> HandleAsync(UpdateMcpToolCommand request, CancellationToken cancellationToken)
    {
        await locks.AcquireAsync(LockKey(request.Id), cancellationToken).ConfigureAwait(false);
        var server = await FindAsync(request.Id, tracked: true, cancellationToken).ConfigureAwait(false);
        var r = request.Request;
        if (!server.Tools.Any(t => t.Name == r.Tool))
        {
            throw new NotFoundException("mcp_tool", r.Tool, $"「{server.Name}」没有工具 {r.Tool}，请先刷新工具列表。");
        }

        if (r.Enabled is { } enabled)
        {
            server.DisabledTools = Toggle(server.DisabledTools, r.Tool, include: !enabled);
        }

        if (r.ReadOnly is { } readOnly)
        {
            server.ReadOnlyTools = Toggle(server.ReadOnlyTools, r.Tool, include: readOnly);
        }

        server.UpdatedAt = clock.GetUtcNow();
        changes.Publish(IntegrationRealtime.McpServer, server.Id, RealtimeChangeKind.Updated);
        return await ToDtoAsync(server, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Unit> HandleAsync(DeleteMcpServerCommand request, CancellationToken cancellationToken)
    {
        var server = await FindAsync(request.Id, tracked: true, cancellationToken).ConfigureAwait(false);
        await EnsureNotReferencedAsync(server, "删除", cancellationToken).ConfigureAwait(false);
        db.McpServers.Remove(server);
        changes.Publish(IntegrationRealtime.McpServer, server.Id, RealtimeChangeKind.Deleted);
        return Unit.Value;
    }

    private static List<string> Toggle(List<string> names, string name, bool include)
        => include ? [.. names.Append(name).Distinct(StringComparer.Ordinal)] : [.. names.Where(n => n != name)];

    private async Task<McpServer> FindAsync(Guid id, bool tracked, CancellationToken cancellationToken)
    {
        var query = tracked ? db.McpServers : db.McpServers.AsNoTracking();
        return await query.FirstOrDefaultAsync(s => s.Id == id, cancellationToken).ConfigureAwait(false)
               ?? throw new NotFoundException("mcp_server", id, "外部服务不存在。");
    }

    private async Task EnsureKeyAvailableAsync(string key, Guid? exceptId, CancellationToken cancellationToken)
    {
        if (await db.McpServers.AnyAsync(s => s.Key == key && s.Id != exceptId, cancellationToken).ConfigureAwait(false))
        {
            throw new ConflictException("mcp_server.duplicate_key", $"标识 {key} 已被其他外部服务使用。");
        }
    }

    /// <summary>
    /// 请求头引用了密钥，就能把它发往服务地址，等同于能读取：新建、改地址、新增引用都需要 integrations.secrets.use。
    /// 地址不变、只沿用已有引用时（改名称、说明、审批设置等）不要求。
    /// </summary>
    private void EnsureCanUseSecrets(McpServer? current, SaveMcpServerRequest r)
    {
        var names = McpServerTools.SecretNames(r.Headers);
        if (names.Count == 0 || tenant.Can(IntegrationFeatures.SecretsUse))
        {
            return;
        }

        var unchanged = current is not null
                        && string.Equals(current.Url, r.Url.Trim(), StringComparison.Ordinal)
                        && names.All(McpServerTools.SecretNames(current.Headers).Contains);
        if (!unchanged)
        {
            throw new ValidationFailedException(new Dictionary<string, string[]>
            {
                ["headers"] = [$"在请求头里引用密钥需要权限 {IntegrationFeatures.SecretsUse}。"],
            });
        }
    }

    /// <summary>
    /// 引用的密钥都要存在。<paramref name="locked"/> 时先拿密钥锁（只能在事务里），检查完到提交之前密钥删不掉。
    /// </summary>
    private async Task EnsureSecretsExistAsync(IReadOnlyList<McpServerHeader> headers, bool locked, CancellationToken cancellationToken)
    {
        var names = McpServerTools.SecretNames(headers);
        IReadOnlyList<string> missing = locked
            ? await secretReferences.LockAndFindMissingAsync(names, cancellationToken).ConfigureAwait(false)
            : [.. names.Except(await secrets.ExistingAsync(names, cancellationToken).ConfigureAwait(false), StringComparer.Ordinal)];
        if (missing.Count > 0)
        {
            throw new ValidationFailedException(new Dictionary<string, string[]>
            {
                ["headers"] = [$"密钥 {string.Join("、", missing)} 不存在，请先在「集成 → 密钥」里添加。"],
            });
        }
    }

    /// <summary>
    /// 工作流按动作键引用工具，改标识或删除服务会让它们失效，先挡住。
    /// </summary>
    private async Task EnsureNotReferencedAsync(McpServer server, string operation, CancellationToken cancellationToken)
    {
        var prefix = McpToolNames.ActionKey(server.Key, string.Empty);
        var workflows = await db.Workflows.AsNoTracking().Select(w => new { w.Name, w.Steps }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var users = workflows
            .Where(w => w.Steps.SelectMany(s => s.SelfAndDescendants())
                .Any(s => s.Kind == WorkflowStepKind.Action && s.ActionKey?.StartsWith(prefix, StringComparison.Ordinal) == true))
            .Select(w => $"「{w.Name}」")
            .ToList();
        if (users.Count > 0)
        {
            throw new ConflictException(
                "mcp_server.in_use",
                $"工作流{string.Join("、", users.Take(5))}{(users.Count > 5 ? $" 等 {users.Count} 个" : string.Empty)}用到了这个服务的工具，先改掉这些步骤再{operation}。");
        }
    }

    private static void Apply(McpServer server, SaveMcpServerRequest r)
    {
        server.Description = r.Description.Trim();
        server.Headers = [.. r.Headers.Select(h => new McpServerHeader(h.Name.Trim(), h.Value))];
        server.IsEnabled = r.IsEnabled;
        server.ApprovalMode = r.ApprovalMode;
    }

    private async Task<McpServerDto> ToDtoAsync(McpServer server, CancellationToken cancellationToken)
        => (await ToDtosAsync([server], cancellationToken).ConfigureAwait(false))[0];

    private async Task<IReadOnlyList<McpServerDto>> ToDtosAsync(IReadOnlyList<McpServer> servers, CancellationToken cancellationToken)
    {
        var names = await ActorNames.ResolveAsync(db, servers.Select(s => s.CreatedBy), cancellationToken).ConfigureAwait(false);
        var canManage = tenant.Can(IntegrationFeatures.Manage);
        return [.. servers.Select(s => new McpServerDto(
            s.Id,
            s.Key,
            s.Name,
            s.Description,
            s.Url,
            [.. s.Headers.Select(h => canManage || !SensitiveHeaders.IsSensitive(h.Name) || SecretTemplate.HasReferences(h.Value) ? h : h with { Value = "***" })],
            s.IsEnabled,
            s.ApprovalMode,
            [.. s.Tools.Select(t =>
            {
                var readOnly = s.ReadOnlyTools.Contains(t.Name);
                return new McpToolDto(
                    t.Name,
                    McpToolNames.ActionKey(s.Key, t.Slug),
                    t.Title,
                    t.Description,
                    readOnly,
                    t.ReadOnlyHint,
                    !s.DisabledTools.Contains(t.Name),
                    McpToolAction.RequiresApproval(s.ApprovalMode, readOnly),
                    t.InputSchema);
            })],
            s.ToolsRefreshedAt,
            s.LastError,
            names(s.CreatedBy),
            s.CreatedAt,
            s.UpdatedAt))];
    }
}

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

[RequireFeature(IntegrationFeatures.Manage)]
public sealed record SetMcpDisabledToolsCommand(Guid Id, IReadOnlyList<string> Tools) : ICommand<McpServerDto>;

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

public sealed class SetMcpDisabledToolsCommandValidator : AbstractValidator<SetMcpDisabledToolsCommand>
{
    public SetMcpDisabledToolsCommandValidator()
        => RuleFor(x => x.Tools).Must(t => t.Count <= McpServerTools.MaxTools).WithMessage($"最多 {McpServerTools.MaxTools} 个工具。").OverridePropertyName("tools");
}

/// <summary>
/// 读取外部服务的工具列表，并转换成保存用的快照。
/// </summary>
internal sealed class McpServerTools(IMcpGateway gateway, ITenantSecrets secrets, ILogger<McpServerTools> logger)
{
    public const int MaxTools = 200;

    private const int MaxDescriptionLength = 4_000;

    private static readonly JsonElement EmptyObjectSchema = PlatformJson.Parse("""{"type":"object","properties":{}}""");

    public async Task<McpEndpoint> EndpointAsync(McpServer server, CancellationToken cancellationToken)
    {
        var headers = await SecretTemplate.RenderHeadersAsync(
            server.Headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)), secrets, text => text, cancellationToken).ConfigureAwait(false);
        return new McpEndpoint(server.Id, new Uri(server.Url), headers);
    }

    /// <summary>
    /// 读取工具列表。失败时返回错误说明，不抛出。
    /// </summary>
    public async Task<(List<McpToolSnapshot>? Tools, string? Error)> DiscoverAsync(McpServer server, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = await EndpointAsync(server, cancellationToken).ConfigureAwait(false);
            var remote = await gateway.ListToolsAsync(endpoint, cancellationToken).ConfigureAwait(false);
            var slugs = McpToolNames.AssignSlugs(remote.Select(t => t.Name));
            var tools = remote
                .DistinctBy(t => t.Name, StringComparer.Ordinal)
                .Take(MaxTools)
                .Select(t => new McpToolSnapshot
                {
                    Name = t.Name,
                    Slug = slugs[t.Name],
                    Title = string.IsNullOrWhiteSpace(t.Title) ? null : t.Title.Trim(),
                    Description = Truncate(t.Description?.Trim() ?? string.Empty, MaxDescriptionLength),
                    InputSchema = t.InputSchema.ValueKind == JsonValueKind.Object ? t.InputSchema.Clone() : EmptyObjectSchema,
                    ReadOnly = t.ReadOnly,
                })
                .ToList();
            var error = remote.Count > MaxTools ? $"服务提供了 {remote.Count} 个工具，只接入前 {MaxTools} 个。" : null;
            return (tools, error);
        }
        catch (Exception ex) when (ex is McpGatewayException or SecretUnavailableException)
        {
            logger.LogInformation("MCP server {ServerId} discovery failed: {Message}", server.Id, ex.Message);
            return (null, ex.Message);
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

internal sealed class McpServerHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    ITenantSecrets secrets,
    McpServerTools tools,
    IUnitOfWork unitOfWork,
    IChangeFeed changes,
    TimeProvider clock) :
    IRequestHandler<ListMcpServersQuery, IReadOnlyList<McpServerDto>>,
    IRequestHandler<GetMcpServerQuery, McpServerDto>,
    IRequestHandler<CreateMcpServerCommand, McpServerDto>,
    IRequestHandler<UpdateMcpServerCommand, McpServerDto>,
    IRequestHandler<RefreshMcpServerToolsCommand, McpServerDto>,
    IRequestHandler<SetMcpDisabledToolsCommand, McpServerDto>,
    IRequestHandler<DeleteMcpServerCommand, Unit>
{
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
        await EnsureSecretsExistAsync(r.Headers, cancellationToken).ConfigureAwait(false);

        var server = new McpServer { Key = key, Name = r.Name.Trim(), Url = r.Url.Trim(), CreatedBy = tenant.Actor };
        Apply(server, r);
        await DiscoverIntoAsync(server, cancellationToken).ConfigureAwait(false);
        return await unitOfWork.RunAsync(async () =>
        {
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

        await EnsureSecretsExistAsync(r.Headers, cancellationToken).ConfigureAwait(false);
        draft.Key = key;
        draft.Name = r.Name.Trim();
        draft.Url = r.Url.Trim();
        Apply(draft, r);
        await DiscoverIntoAsync(draft, cancellationToken).ConfigureAwait(false);

        return await unitOfWork.RunAsync(async () =>
        {
            var server = await FindAsync(request.Id, tracked: true, cancellationToken).ConfigureAwait(false);
            server.Key = draft.Key;
            server.Name = draft.Name;
            server.Url = draft.Url;
            Apply(server, r);
            CopyDiscovery(draft, server);
            server.UpdatedAt = clock.GetUtcNow();
            changes.Publish(IntegrationRealtime.McpServer, server.Id, RealtimeChangeKind.Updated);
            return await ToDtoAsync(server, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpServerDto> HandleAsync(RefreshMcpServerToolsCommand request, CancellationToken cancellationToken)
    {
        var draft = await FindAsync(request.Id, tracked: false, cancellationToken).ConfigureAwait(false);
        await DiscoverIntoAsync(draft, cancellationToken).ConfigureAwait(false);
        return await unitOfWork.RunAsync(async () =>
        {
            var server = await FindAsync(request.Id, tracked: true, cancellationToken).ConfigureAwait(false);
            CopyDiscovery(draft, server);
            server.UpdatedAt = clock.GetUtcNow();
            changes.Publish(IntegrationRealtime.McpServer, server.Id, RealtimeChangeKind.Updated);
            return await ToDtoAsync(server, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpServerDto> HandleAsync(SetMcpDisabledToolsCommand request, CancellationToken cancellationToken)
    {
        var server = await FindAsync(request.Id, tracked: true, cancellationToken).ConfigureAwait(false);
        var known = server.Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        server.DisabledTools = [.. request.Tools.Where(known.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
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

    private async Task EnsureSecretsExistAsync(IReadOnlyList<McpServerHeader> headers, CancellationToken cancellationToken)
    {
        var names = headers.SelectMany(h => SecretTemplate.References(h.Value)).Distinct(StringComparer.Ordinal).ToList();
        var existing = await secrets.ExistingAsync(names, cancellationToken).ConfigureAwait(false);
        var missing = names.Where(n => !existing.Contains(n)).ToList();
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

    private async Task DiscoverIntoAsync(McpServer server, CancellationToken cancellationToken)
    {
        var (discovered, error) = await tools.DiscoverAsync(server, cancellationToken).ConfigureAwait(false);
        if (discovered is not null)
        {
            server.Tools = discovered;
            server.DisabledTools = [.. server.DisabledTools.Where(name => discovered.Any(t => t.Name == name))];
            server.ToolsRefreshedAt = clock.GetUtcNow();
        }

        server.LastError = error;
    }

    private static void CopyDiscovery(McpServer from, McpServer to)
    {
        to.Tools = from.Tools;
        to.DisabledTools = from.DisabledTools;
        to.ToolsRefreshedAt = from.ToolsRefreshedAt;
        to.LastError = from.LastError;
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
            [.. s.Tools.Select(t => new McpToolDto(
                t.Name,
                McpToolNames.ActionKey(s.Key, t.Slug),
                t.Title,
                t.Description,
                t.ReadOnly,
                !s.DisabledTools.Contains(t.Name),
                McpToolAction.RequiresApproval(s.ApprovalMode, t.ReadOnly),
                t.InputSchema))],
            s.ToolsRefreshedAt,
            s.LastError,
            names(s.CreatedBy),
            s.CreatedAt,
            s.UpdatedAt))];
    }
}

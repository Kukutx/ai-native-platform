using System.Text.Json;
using AiNative.Contracts.Common;
using AiNative.Domain.Integrations;

namespace AiNative.Contracts.Integrations;

/// <summary>
/// 密钥只返回名称和说明，任何接口都拿不到明文。
/// </summary>
public sealed record TenantSecretDto(
    Guid Id,
    string Name,
    string Description,
    ActorDto CreatedBy,
    ActorDto UpdatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateTenantSecretRequest
{
    /// <summary>
    /// 引用名：大写字母开头，只含大写字母、数字和下划线，例如 GITHUB_TOKEN。
    /// </summary>
    public required string Name { get; init; }

    public required string Value { get; init; }

    public string Description { get; init; } = string.Empty;
}

public sealed record UpdateTenantSecretRequest
{
    /// <summary>
    /// 新的值；不传表示只改说明。
    /// </summary>
    public string? Value { get; init; }

    public string Description { get; init; } = string.Empty;
}

/// <param name="Key">平台里的动作键，形如 mcp.github.create_issue。</param>
/// <param name="ReadOnly">管理员确认它只读。</param>
/// <param name="ReadOnlyHint">服务自己声明只读，只作参考。</param>
/// <param name="Enabled">管理员没有关掉它。</param>
public sealed record McpToolDto(
    string Name,
    string Key,
    string? Title,
    string Description,
    bool ReadOnly,
    bool ReadOnlyHint,
    bool Enabled,
    bool RequiresApproval,
    JsonElement InputSchema);

/// <param name="Headers">没有管理权限时，敏感请求头里直接写的值显示为 ***（引用密钥的写法照常显示）。</param>
public sealed record McpServerDto(
    Guid Id,
    string Key,
    string Name,
    string Description,
    string Url,
    IReadOnlyList<McpServerHeader> Headers,
    bool IsEnabled,
    McpApprovalMode ApprovalMode,
    IReadOnlyList<McpToolDto> Tools,
    DateTimeOffset? ToolsRefreshedAt,
    string? LastError,
    ActorDto CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SaveMcpServerRequest
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    public string Description { get; init; } = string.Empty;

    public required string Url { get; init; }

    public IReadOnlyList<McpServerHeader> Headers { get; init; } = [];

    public bool IsEnabled { get; init; } = true;

    public McpApprovalMode ApprovalMode { get; init; } = McpApprovalMode.WritesOnly;
}

/// <summary>
/// 调整一个工具，只改给出的项。
/// </summary>
public sealed record UpdateMcpToolRequest
{
    /// <summary>
    /// 外部服务里的工具名。
    /// </summary>
    public required string Tool { get; init; }

    /// <summary>
    /// 是否可用。
    /// </summary>
    public bool? Enabled { get; init; }

    /// <summary>
    /// 管理员确认它只读：「只读工具免审批」模式下智能体可以直接调用。
    /// </summary>
    public bool? ReadOnly { get; init; }
}

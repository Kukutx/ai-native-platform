using System.Text.Json;
using AiNative.Domain.Common;

namespace AiNative.Domain.Integrations;

/// <summary>
/// 智能体调用外部工具时是否需要人工审批。成员自己在对话或工作流里使用不受影响。
/// </summary>
public enum McpApprovalMode
{
    /// <summary>
    /// 都不需要审批。
    /// </summary>
    Never = 0,

    /// <summary>
    /// 管理员确认为只读的工具直接执行，其余需要审批。服务自己声明的只读不算数。
    /// </summary>
    WritesOnly = 1,

    /// <summary>
    /// 每次调用都需要审批。
    /// </summary>
    Always = 2,
}

/// <summary>
/// 请求头。值里可以用 {{ secrets.名称 }} 引用密钥。
/// </summary>
public sealed record McpServerHeader(string Name, string Value);

/// <summary>
/// 从外部服务读到的工具，保存一份快照：列出工具不必每次访问外部服务，外部服务暂时连不上也不影响列表。
/// </summary>
public sealed record McpToolSnapshot
{
    /// <summary>
    /// 外部服务里的工具名，调用时原样传回。
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// 平台里的工具标识：工具名里不允许的字符替换掉、过长时截断加哈希，用在动作键 mcp.&lt;服务&gt;.&lt;标识&gt; 里。
    /// </summary>
    public required string Slug { get; init; }

    public string? Title { get; init; }

    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// 输入参数的 JSON Schema。
    /// </summary>
    public JsonElement InputSchema { get; init; }

    /// <summary>
    /// 服务声明工具只读（readOnlyHint）。MCP 规定工具标注不可信，只在界面上给管理员作参考，不参与审批判断。
    /// </summary>
    public bool ReadOnlyHint { get; init; }
}

/// <summary>
/// 外部 MCP 服务（Streamable HTTP 或 SSE）。它提供的工具成为平台动作，智能体、对话和工作流都能调用，
/// 权限、审批和执行记录与内置动作一致。
/// </summary>
public sealed class McpServer : TenantEntity
{
    /// <summary>
    /// 服务标识，2 到 20 个小写字母或数字、字母开头，例如 github。用在工具名 mcp_&lt;标识&gt;_&lt;工具&gt; 里，所以不能含下划线。
    /// </summary>
    public required string Key { get; set; }

    public required string Name { get; set; }

    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 服务地址，http(s)。
    /// </summary>
    public required string Url { get; set; }

    public List<McpServerHeader> Headers { get; set; } = [];

    /// <summary>
    /// 停用后它的工具不再出现在任何地方。
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    public McpApprovalMode ApprovalMode { get; set; } = McpApprovalMode.WritesOnly;

    /// <summary>
    /// 被管理员关掉的工具（按外部工具名）。新出现的工具默认可用；关掉的工具暂时从服务里消失后再出现，仍然关着。
    /// </summary>
    public List<string> DisabledTools { get; set; } = [];

    /// <summary>
    /// 管理员确认为只读的工具（按外部工具名）：「只读工具免审批」模式下智能体可以直接调用。
    /// </summary>
    public List<string> ReadOnlyTools { get; set; } = [];

    public List<McpToolSnapshot> Tools { get; set; } = [];

    /// <summary>
    /// 最近一次成功读取工具列表的时间。
    /// </summary>
    public DateTimeOffset? ToolsRefreshedAt { get; set; }

    /// <summary>
    /// 最近一次读取工具列表失败的原因；成功后清空。
    /// </summary>
    public string? LastError { get; set; }

    public Actor CreatedBy { get; set; }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiNative.Application.Common;

namespace AiNative.Application.Integrations;

/// <summary>
/// 连接一个外部 MCP 服务所需的信息。请求头里的密钥已经换成明文，只在内存里存在。
/// </summary>
public sealed record McpEndpoint(Guid ServerId, Uri Url, IReadOnlyDictionary<string, string> Headers);

public sealed record McpRemoteTool(string Name, string? Title, string? Description, JsonElement InputSchema, bool ReadOnly);

/// <param name="Text">文本内容拼在一起（图片等非文本内容给出占位说明）。</param>
/// <param name="Structured">服务给出的结构化结果。</param>
public sealed record McpCallResult(bool IsError, string Text, JsonElement? Structured);

/// <summary>
/// 外部服务连不上、超时、协议出错。
/// </summary>
public sealed class McpGatewayException(string message) : AppException("integrations.mcp_unavailable", message);

/// <summary>
/// 访问外部 MCP 服务（Streamable HTTP，兼容旧的 SSE）。实现负责连接复用、超时和内网防护。
/// </summary>
public interface IMcpGateway
{
    Task<IReadOnlyList<McpRemoteTool>> ListToolsAsync(McpEndpoint endpoint, CancellationToken cancellationToken);

    Task<McpCallResult> CallToolAsync(McpEndpoint endpoint, string toolName, JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>
/// 外部工具在平台里的命名。动作键 mcp.&lt;服务标识&gt;.&lt;工具标识&gt;，给模型的工具名把点换成下划线：
/// 服务标识不含下划线，工具名就不会和别的服务撞车；整体不超过模型接口要求的 64 个字符。
/// </summary>
public static partial class McpToolNames
{
    public const string KeyPrefix = "mcp.";

    public const int MaxSlugLength = 39;

    [GeneratedRegex("^[a-z][a-z0-9]{1,19}$")]
    public static partial Regex ServerKeyPattern();

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex InvalidChars();

    public static string ActionKey(string serverKey, string slug) => $"{KeyPrefix}{serverKey}.{slug}";

    /// <summary>
    /// 给一批外部工具名分配工具标识：不允许的字符换成下划线，过长或换完撞车的截断后加上原名的哈希。
    /// 结果只取决于这批名字本身，刷新工具列表时已有工具的标识不变，工作流和审批里的引用继续有效。
    /// </summary>
    public static IReadOnlyDictionary<string, string> AssignSlugs(IEnumerable<string> toolNames)
    {
        var names = toolNames.Distinct(StringComparer.Ordinal).ToList();
        var sanitized = names.ToDictionary(n => n, Sanitize, StringComparer.Ordinal);
        var collisions = sanitized.GroupBy(kv => kv.Value, StringComparer.Ordinal).Where(g => g.Count() > 1).SelectMany(g => g.Select(kv => kv.Key))
            .ToHashSet(StringComparer.Ordinal);
        var slugs = names.ToDictionary(
            n => n,
            n => collisions.Contains(n) || sanitized[n].Length > MaxSlugLength ? Hashed(sanitized[n], n, 8) : sanitized[n],
            StringComparer.Ordinal);

        // 加了哈希的标识恰好和别的工具名相同时，换成更长的哈希。
        foreach (var group in slugs.GroupBy(kv => kv.Value, StringComparer.Ordinal).Where(g => g.Count() > 1).ToList())
        {
            foreach (var (name, _) in group)
            {
                slugs[name] = Hashed(sanitized[name], name, 12);
            }
        }

        return slugs;
    }

    private static string Sanitize(string name)
    {
        var slug = InvalidChars().Replace(name, "_");
        return slug.Length == 0 ? "tool" : slug;
    }

    private static string Hashed(string slug, string name, int hashLength)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..hashLength];
        var prefixLength = MaxSlugLength - hashLength - 1;
        return $"{slug[..Math.Min(slug.Length, prefixLength)]}_{hash}";
    }
}

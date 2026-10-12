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

/// <param name="ReadOnlyHint">服务声明工具只读。MCP 规定工具标注不可信，只作参考。</param>
public sealed record McpRemoteTool(string Name, string? Title, string? Description, JsonElement InputSchema, bool ReadOnlyHint);

/// <param name="Tools">按服务返回的顺序，工具名不重复，最多为请求的数量。</param>
/// <param name="HasMore">服务还有更多工具没有读取。</param>
public sealed record McpToolList(IReadOnlyList<McpRemoteTool> Tools, bool HasMore);

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
    /// <summary>
    /// 读取工具列表，读够 <paramref name="maxTools"/> 个就停止翻页，不把服务给的全部工具都读进内存。
    /// </summary>
    Task<McpToolList> ListToolsAsync(McpEndpoint endpoint, int maxTools, CancellationToken cancellationToken);

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
    /// <paramref name="previous"/> 里已有的工具沿用原来的标识，不受新出现的同名工具影响：
    /// 工作流和审批按动作键引用工具，刷新工具列表后这些引用继续有效。
    /// </summary>
    public static IReadOnlyDictionary<string, string> AssignSlugs(IEnumerable<string> toolNames, IReadOnlyDictionary<string, string>? previous = null)
    {
        var names = toolNames.Distinct(StringComparer.Ordinal).ToList();
        var slugs = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (previous?.TryGetValue(name, out var slug) == true && taken.Add(slug))
            {
                slugs[name] = slug;
            }
        }

        var fresh = names.Where(n => !slugs.ContainsKey(n)).ToList();
        var sanitized = fresh.ToDictionary(n => n, Sanitize, StringComparer.Ordinal);
        var collisions = sanitized.GroupBy(kv => kv.Value, StringComparer.Ordinal).Where(g => g.Count() > 1).SelectMany(g => g.Select(kv => kv.Key))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var name in fresh)
        {
            var plain = sanitized[name];
            var slug = collisions.Contains(name) || plain.Length > MaxSlugLength || taken.Contains(plain) ? Hashed(plain, name, 8) : plain;

            // 加了哈希的标识恰好和别的工具撞上时，换成更长的哈希。
            for (var hashLength = 12; !taken.Add(slug) && hashLength <= 32; hashLength += 4)
            {
                slug = Hashed(plain, name, hashLength);
            }

            slugs[name] = slug;
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

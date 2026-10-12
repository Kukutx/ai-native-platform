using AiNative.Application.Common;
using AiNative.Application.Integrations;
using AiNative.Domain.Workflows;

namespace AiNative.Application.Workflows;

/// <summary>
/// HTTP 步骤的凭据保护。
/// </summary>
internal static class HttpStepSecurity
{
    public const string Masked = "***";

    /// <summary>
    /// 地址里模板之前的固定部分已经写全了协议、主机和端口时，返回这个来源（例如 https://api.example.com）；否则返回 null。
    /// 请求头引用密钥的步骤要求来源固定，防止触发数据或模型输出通过模板把密钥引到别的主机。
    /// </summary>
    public static string? LiteralOrigin(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var text = url.Trim();
        var templateAt = text.IndexOf("{{", StringComparison.Ordinal);
        var fixedPart = templateAt < 0 ? text : text[..templateAt];
        var schemeEnd = fixedPart.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return null;
        }

        var authorityEnd = fixedPart.IndexOfAny(['/', '?', '#'], schemeEnd + 3);
        if (authorityEnd < 0)
        {
            // 主机后面紧跟模板（例如 https://api.example.com{{ x }}）时主机仍可能被拼接改写。
            if (templateAt >= 0)
            {
                return null;
            }

            authorityEnd = fixedPart.Length;
        }

        return Uri.TryCreate(fixedPart[..authorityEnd], UriKind.Absolute, out var origin)
               && (origin.Scheme == Uri.UriSchemeHttp || origin.Scheme == Uri.UriSchemeHttps)
               && string.IsNullOrEmpty(origin.UserInfo)
            ? origin.GetLeftPart(UriPartial.Authority)
            : null;
    }

    public static bool HasSecretHeaders(WorkflowStep step)
        => step.Headers?.Values.Any(SecretTemplate.HasReferences) == true;

    public static bool SameOrigin(Uri uri, string origin)
        => string.Equals(uri.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 给没有修改权限的人看的步骤：直接写在敏感请求头里的值（例如 Authorization: Bearer xxx）打码，
    /// 引用密钥的写法本身不含密钥，原样保留。
    /// </summary>
    public static List<WorkflowStep> MaskSensitiveHeaders(IReadOnlyList<WorkflowStep> steps)
        => [.. steps.Select(Mask)];

    private static WorkflowStep Mask(WorkflowStep step) => step with
    {
        Headers = step.Headers?.ToDictionary(
            h => h.Key,
            h => SensitiveHeaders.IsSensitive(h.Key) && !SecretTemplate.HasReferences(h.Value) ? Masked : h.Value,
            StringComparer.OrdinalIgnoreCase),
        Steps = step.Steps is { } children ? MaskSensitiveHeaders(children) : null,
        Branches = step.Branches?.Select(b => b with { Steps = MaskSensitiveHeaders(b.Steps) }).ToList(),
    };
}

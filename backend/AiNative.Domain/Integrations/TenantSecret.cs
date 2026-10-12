using AiNative.Domain.Common;

namespace AiNative.Domain.Integrations;

/// <summary>
/// 空间级密钥：外部服务的令牌、API Key 等。值加密保存，任何接口都不返回明文，
/// 只能在外部 MCP 服务和工作流 HTTP 请求的请求头里以 {{ secrets.名称 }} 引用，发请求时才解密。
/// </summary>
public sealed class TenantSecret : TenantEntity
{
    /// <summary>
    /// 引用名，大写字母开头，只含大写字母、数字和下划线，例如 GITHUB_TOKEN。创建后不能改名（密文和名称绑定）。
    /// </summary>
    public required string Name { get; init; }

    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 加密后的值（含密钥版本、随机数和校验标签）。
    /// </summary>
    public required byte[] CipherText { get; set; }

    public Actor CreatedBy { get; set; }

    public Actor UpdatedBy { get; set; }
}

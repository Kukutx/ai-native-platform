namespace AiNative.Domain.Common;

/// <summary>
/// 违反领域规则时抛出；API 层统一映射为 409/422 一类的业务错误。
/// </summary>
public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

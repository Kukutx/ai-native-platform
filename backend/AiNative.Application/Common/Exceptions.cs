namespace AiNative.Application.Common;

/// <summary>
/// 应用层错误的基类，API 统一转换成 ProblemDetails，Code 供前端做精确处理。
/// </summary>
public abstract class AppException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class NotFoundException(string entity, object id, string? message = null)
    : AppException($"{entity}.not_found", message ?? $"{entity} {id} 不存在。");

public sealed class ForbiddenException(string message = "没有执行该操作的权限。", string code = "forbidden")
    : AppException(code, message);

public sealed class ConflictException(string code, string message) : AppException(code, message);

/// <summary>
/// 调用过于频繁（API 映射为 429）。
/// </summary>
public sealed class RateLimitedException(string code, string message) : AppException(code, message);

public sealed class UnauthorizedException(string message = "身份验证失败。")
    : AppException("unauthorized", message);

public sealed class ValidationFailedException(IReadOnlyDictionary<string, string[]> errors)
    : AppException("validation_failed", "请求参数不合法。")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

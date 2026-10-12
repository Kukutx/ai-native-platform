using AiNative.Application.Common;
using AiNative.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace AiNative.Api.Errors;

/// <summary>
/// 应用层与领域层异常统一转换成 ProblemDetails，扩展字段 code 供客户端精确处理。
/// </summary>
internal sealed class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var postgres = FindPostgresError(exception);
        var (status, code, title) = postgres switch
        {
            // 数据库约束兜底：并发插入撞上唯一索引、超出列长度等，不应以 500 呈现给调用方
            { SqlState: PostgresErrorCodes.UniqueViolation } => (StatusCodes.Status409Conflict, "duplicate", "记录已存在或与其他记录冲突，请刷新后重试。"),
            { SqlState: PostgresErrorCodes.StringDataRightTruncation } => (StatusCodes.Status400BadRequest, "value_too_long", "输入内容超出长度限制。"),
            { SqlState: PostgresErrorCodes.ForeignKeyViolation } => (StatusCodes.Status409Conflict, "reference_conflict", "关联的记录不存在或已被删除，请刷新后重试。"),
            { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure } => (StatusCodes.Status409Conflict, "concurrent_update", "操作与其他同时进行的修改冲突，请重试。"),
            _ => Map(exception, httpContext),
        };

        if (status >= 500)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }
        else if (postgres is not null)
        {
            // 走到数据库约束说明前面的校验漏了，记下来便于补校验
            logger.LogWarning("Database constraint {SqlState} ({Constraint}) for {Method} {Path}", postgres.SqlState, postgres.ConstraintName, httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;
        var details = exception is ValidationFailedException validation
            ? new ValidationProblemDetails(validation.Errors.ToDictionary(e => e.Key, e => e.Value)) { Title = title, Status = status }
            : new ProblemDetails { Title = title, Status = status };
        details.Extensions["code"] = code;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = details,
            Exception = exception,
        }).ConfigureAwait(false);
    }

    private static (int Status, string Code, string Title) Map(Exception exception, HttpContext httpContext) =>
        exception switch
        {
            ValidationFailedException ex => (StatusCodes.Status400BadRequest, ex.Code, ex.Message),
            NotFoundException ex => (StatusCodes.Status404NotFound, ex.Code, ex.Message),
            ForbiddenException ex => (StatusCodes.Status403Forbidden, ex.Code, ex.Message),
            UnauthorizedException ex => (StatusCodes.Status401Unauthorized, ex.Code, ex.Message),
            ConflictException ex => (StatusCodes.Status409Conflict, ex.Code, ex.Message),
            RateLimitedException ex => (StatusCodes.Status429TooManyRequests, ex.Code, ex.Message),
            AppException ex => (StatusCodes.Status400BadRequest, ex.Code, ex.Message),
            DomainException ex => (StatusCodes.Status422UnprocessableEntity, ex.Code, ex.Message),
            BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } => (StatusCodes.Status413PayloadTooLarge, "request_too_large", "上传的内容太大。"),
            BadHttpRequestException ex => (ex.StatusCode, "bad_request", ex.Message),
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested => (499, "client_closed", "请求已取消。"),
            _ => (StatusCodes.Status500InternalServerError, "internal_error", "服务器内部错误。"),
        };

    /// <summary>EF 会把数据库错误包在 DbUpdateException 等异常里，沿内层异常链查找。</summary>
    private static PostgresException? FindPostgresError(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }
}

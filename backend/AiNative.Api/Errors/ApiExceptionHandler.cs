using AiNative.Application.Common;
using AiNative.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AiNative.Api.Errors;

/// <summary>
/// 应用层与领域层异常统一转换成 ProblemDetails，扩展字段 code 供客户端精确处理。
/// </summary>
internal sealed class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, title) = exception switch
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

        if (status >= 500)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
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
}

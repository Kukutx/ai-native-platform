using AiNative.Application.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace AiNative.Api.Controllers;

/// <summary>
/// 控制器只做协议转换：把 HTTP 请求变成应用层请求并分发，权限、校验、事务都在请求管道里统一处理。
/// </summary>
[ApiController]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    protected Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request)
        => HttpContext.RequestServices.GetRequiredService<IRequestDispatcher>().SendAsync(request, HttpContext.RequestAborted);
}

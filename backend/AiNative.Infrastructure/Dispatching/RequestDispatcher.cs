using System.Collections.Concurrent;
using AiNative.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AiNative.Infrastructure.Dispatching;

internal interface IRequestHandlerWrapper
{
    Task<object?> HandleAsync(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

internal sealed class RequestHandlerWrapper<TRequest, TResponse> : IRequestHandlerWrapper
    where TRequest : IRequest<TResponse>
{
    public async Task<object?> HandleAsync(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;
        var handler = serviceProvider.GetRequiredService<IRequestHandler<TRequest, TResponse>>();
        RequestHandlerDelegate<TResponse> next = () => handler.HandleAsync(typedRequest, cancellationToken);

        foreach (var behavior in serviceProvider.GetServices<IRequestPipelineBehavior<TRequest, TResponse>>().Reverse())
        {
            var current = next;
            next = () => behavior.HandleAsync(typedRequest, current, cancellationToken);
        }

        return await next().ConfigureAwait(false);
    }
}

/// <summary>
/// 轻量请求分发：按请求类型找到处理器，依次穿过校验、日志、事务等管道行为。
/// </summary>
internal sealed class RequestDispatcher(IServiceProvider serviceProvider) : IRequestDispatcher
{
    private static readonly ConcurrentDictionary<(Type Request, Type Response), IRequestHandlerWrapper> Wrappers = new();

    public async Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var wrapper = Wrappers.GetOrAdd((request.GetType(), typeof(TResponse)), static pair =>
            (IRequestHandlerWrapper)Activator.CreateInstance(typeof(RequestHandlerWrapper<,>).MakeGenericType(pair.Request, pair.Response))!);
        return (TResponse)(await wrapper.HandleAsync(request, serviceProvider, cancellationToken).ConfigureAwait(false))!;
    }
}

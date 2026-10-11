namespace AiNative.Application.Abstractions;

public interface IRequest<out TResponse>;

/// <summary>
/// 写操作：由请求管道包一层事务，处理器结束后统一保存并提交，提交成功后再执行实时推送等后续动作。
/// </summary>
public interface ICommand<out TResponse> : IRequest<TResponse>;

/// <summary>
/// 只读请求：不开事务。
/// </summary>
public interface IQuery<out TResponse> : IRequest<TResponse>;

public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken);
}

public interface IRequestDispatcher
{
    Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}

public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();

public interface IRequestPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> HandleAsync(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}

/// <summary>
/// 请求管道之外（后台执行）需要事务时使用，语义与写操作的事务管道相同：结束后保存并提交，提交成功后才执行登记的后续动作；
/// 失败回滚并清空跟踪状态。
/// </summary>
public interface IUnitOfWork
{
    Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default);
}

public readonly record struct Unit
{
    public static Unit Value => default;
}

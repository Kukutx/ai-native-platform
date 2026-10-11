using System.Diagnostics;
using AiNative.Application.Abstractions;
using AiNative.Application.Common;
using AiNative.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.Extensions.Logging;

namespace AiNative.Infrastructure.Dispatching;

/// <summary>
/// 执行 FluentValidation 校验，失败统一抛 ValidationFailedException（API 映射为 400）。
/// </summary>
internal sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IRequestPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> HandleAsync(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
            failures.AddRange(result.Errors);
        }

        if (failures.Count > 0)
        {
            throw new ValidationFailedException(failures
                .GroupBy(f => f.PropertyName.Replace("Request.", string.Empty, StringComparison.Ordinal))
                .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).Distinct().ToArray()));
        }

        return await next().ConfigureAwait(false);
    }
}

/// <summary>
/// 写操作的事务边界：处理器结束后保存并提交，提交成功才执行登记的后续动作；失败回滚并清空跟踪状态。
/// 已处于事务中时直接执行（外层负责提交）。
/// </summary>
internal sealed class TransactionBehavior<TRequest, TResponse>(AppDbContext db, PostCommitActions postCommit)
    : IRequestPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> HandleAsync(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not ICommand<TResponse> || db.Database.CurrentTransaction is not null)
        {
            return await next().ConfigureAwait(false);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        postCommit.Begin();
        try
        {
            var response = await next().ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            await postCommit.CompleteAsync(cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch
        {
            postCommit.Abort();
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            throw;
        }
    }
}

internal sealed class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IRequestPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private static readonly TimeSpan SlowThreshold = TimeSpan.FromMilliseconds(500);

    public async Task<TResponse> HandleAsync(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await next().ConfigureAwait(false);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (elapsed > SlowThreshold)
            {
                logger.LogWarning("Slow request {Request} took {Elapsed} ms", typeof(TRequest).Name, (int)elapsed.TotalMilliseconds);
            }
        }
    }
}

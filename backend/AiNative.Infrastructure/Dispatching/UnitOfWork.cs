using AiNative.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AiNative.Infrastructure.Dispatching;

/// <summary>
/// 请求管道之外（后台任务）需要事务时使用：与 TransactionBehavior 相同的语义，提交后再执行通知类动作。
/// </summary>
internal static class UnitOfWork
{
    public static async Task<T> RunAsync<T>(IServiceProvider services, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var postCommit = services.GetRequiredService<PostCommitActions>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        postCommit.Begin();
        try
        {
            var result = await work().ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            await postCommit.CompleteAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            postCommit.Abort();
            throw;
        }
    }
}

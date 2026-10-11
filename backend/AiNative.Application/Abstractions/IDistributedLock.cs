namespace AiNative.Application.Abstractions;

/// <summary>
/// 跨实例的互斥锁。拿不到时立即返回 null，不排队等待；释放（Dispose）后其他人才能拿到。
/// </summary>
public interface IDistributedLock
{
    Task<IAsyncDisposable?> TryAcquireAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// 事务级互斥锁：在当前事务内排队等待，事务提交或回滚时自动释放。用于"先查再插入"这类必须串行的写操作。
/// </summary>
public interface ITransactionLock
{
    Task AcquireAsync(string key, CancellationToken cancellationToken = default);
}

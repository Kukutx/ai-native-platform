namespace AiNative.Application.Abstractions;

/// <summary>
/// 跨实例的互斥锁。拿不到时立即返回 null，不排队等待；释放（Dispose）后其他人才能拿到。
/// </summary>
public interface IDistributedLock
{
    Task<IAsyncDisposable?> TryAcquireAsync(string key, CancellationToken cancellationToken = default);
}

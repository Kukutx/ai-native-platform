using AiNative.Application.Abstractions;
using AiNative.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Infrastructure.Cluster;

/// <summary>
/// 本进程的集群身份与租约。
/// </summary>
internal sealed class ClusterNode : IClusterNode
{
    private CancellationTokenSource _lease = new();
    private volatile bool _registered;
    private volatile bool _hasPeers;

    public Guid InstanceId { get; } = Guid.CreateVersion7();

    public string HostName { get; } = Environment.MachineName;

    /// <summary>
    /// 已在数据库登记且租约有效。之前不领取运行：否则别的实例会把它领取的运行当成无主的收回。
    /// </summary>
    public bool IsRegistered => _registered;

    /// <summary>
    /// 是否还有其他在线实例。只有一个实例时不必广播，单实例部署没有额外开销。
    /// </summary>
    public bool HasPeers => _hasPeers;

    /// <summary>
    /// 租约失效（长时间没报到成功，可能已被其他实例判定为失联）时取消。正在执行的运行随之停止，
    /// 不与接手的实例重复执行；重新登记后换成新的令牌。
    /// </summary>
    public CancellationToken LeaseToken => Volatile.Read(ref _lease).Token;

    internal void MarkRegistered() => _registered = true;

    internal void SetHasPeers(bool value) => _hasPeers = value;

    internal void RevokeLease()
    {
        _registered = false;
        var previous = Interlocked.Exchange(ref _lease, new CancellationTokenSource());
        try
        {
            previous.Cancel();
        }
        catch (AggregateException)
        {
            // 取消回调里的异常由各自的执行流程处理，这里只负责发出信号。
        }
    }
}

internal static class ClusterLiveness
{
    /// <summary>
    /// 判断实例是否存活的时间界线：数据库当前时间减去超时，报到时间也取数据库时间，不受各实例时钟偏差影响。
    /// 报到时间晚于界线的实例视为存活；判断写在条件更新的子查询里，能看到刚登记的实例，不会误收新实例的运行。
    /// </summary>
    public static async Task<DateTimeOffset> CutoffAsync(AppDbContext db, TimeSpan timeout, CancellationToken cancellationToken)
        => await NowAsync(db, cancellationToken).ConfigureAwait(false) - timeout;

    public static Task<DateTimeOffset> NowAsync(AppDbContext db, CancellationToken cancellationToken)
        => db.Database.SqlQuery<DateTimeOffset>($"SELECT now() AS \"Value\"").SingleAsync(cancellationToken);
}

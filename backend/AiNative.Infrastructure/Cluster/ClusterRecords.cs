namespace AiNative.Infrastructure.Cluster;

/// <summary>
/// 一个在线（或刚失联）的服务实例。实例定期刷新 <see cref="LastSeenAt"/>（数据库时间），
/// 超过 <see cref="ClusterOptions.InstanceTimeout"/> 没有刷新的视为失联。
/// </summary>
internal sealed class ClusterInstance
{
    public Guid Id { get; set; }

    public string HostName { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>
/// 超过 NOTIFY 载荷上限的广播消息正文，通知里只带这一行的 ID，几分钟后清理。
/// </summary>
internal sealed class ClusterMessage
{
    public Guid Id { get; set; }

    public string Payload { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

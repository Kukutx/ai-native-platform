namespace AiNative.Infrastructure.Cluster;

public sealed class ClusterOptions
{
    public const string SectionName = "Cluster";

    /// <summary>
    /// 实例多久向数据库报到一次。
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 超过这个时间没有报到的实例视为失联，它领取的运行由存活的实例接手。
    /// 要明显大于报到间隔，数据库偶尔变慢时才不会误判；实例自己连续这么久的一半没报到成功，会先停下手里的运行。
    /// </summary>
    public TimeSpan InstanceTimeout { get; set; } = TimeSpan.FromSeconds(60);
}

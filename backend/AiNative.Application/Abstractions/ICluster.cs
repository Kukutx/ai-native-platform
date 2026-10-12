using System.Text.Json;

namespace AiNative.Application.Abstractions;

/// <summary>
/// 当前进程在集群里的身份。每次启动生成新的标识，领取的运行、执行的审批都记上它，
/// 实例失联后存活的实例据此接手。
/// </summary>
public interface IClusterNode
{
    Guid InstanceId { get; }
}

/// <summary>
/// 实例之间的广播（PostgreSQL LISTEN/NOTIFY）。消息只发给其他实例，本实例要做的事请直接在本地完成；
/// 投递是尽力而为的：数据库短暂不可用时消息会丢失，依赖它的地方要有兜底（缓存过期、定时轮询）。
/// </summary>
public interface IClusterBus
{
    void Publish<T>(string topic, T payload);
}

/// <summary>
/// 处理其他实例广播来的某个主题的消息。注册为单例，按 <see cref="Topic"/> 分发。
/// </summary>
public interface IClusterMessageHandler
{
    string Topic { get; }

    Task HandleAsync(JsonElement payload, CancellationToken cancellationToken);
}

public static class ClusterTopics
{
    /// <summary>
    /// 实体变化推送，载荷为空间 ID 与变化内容。
    /// </summary>
    public const string RealtimeChange = "realtime.change";

    /// <summary>
    /// 智能体执行过程的事件推送。
    /// </summary>
    public const string RealtimeRunEvent = "realtime.run_event";

    /// <summary>
    /// 成员权限缓存失效，载荷为成员身份 ID 列表。
    /// </summary>
    public const string AccessCacheInvalidated = "cache.access";

    /// <summary>
    /// API 密钥缓存失效，载荷为密钥前缀。
    /// </summary>
    public const string ApiKeyCacheInvalidated = "cache.api_key";

    /// <summary>
    /// 有新的智能体唤醒入队。
    /// </summary>
    public const string WakeupQueued = "signal.wakeup";

    /// <summary>
    /// 有新的工作流运行可以执行。
    /// </summary>
    public const string WorkflowQueued = "signal.workflow";

    /// <summary>
    /// 有实例刚刚上线。只有一个实例时不广播，收到后其他实例开始广播。
    /// </summary>
    public const string InstanceJoined = "cluster.joined";
}

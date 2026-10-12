namespace AiNative.Application.Workflows;

public sealed class WorkflowOptions
{
    public const string SectionName = "Workflows";

    /// <summary>
    /// 关闭后本进程不执行工作流（例如只承担 API 流量的实例）。
    /// </summary>
    public bool Enabled { get; set; } = true;

    public int MaxConcurrentRuns { get; set; } = 8;

    /// <summary>
    /// 没有信号时的兜底轮询间隔（延时到期、等待超时靠它发现）。
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 检查定时触发的间隔。cron 精确到分钟，这个值决定最大延迟。
    /// </summary>
    public TimeSpan ScheduleSweepInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 工作流步骤引发的事件再触发工作流的最大层数，超过就不再触发，防止互相触发形成循环。
    /// </summary>
    public int MaxTriggerDepth { get; set; } = 3;

    /// <summary>
    /// 单个工作流每分钟最多被事件触发的次数，超出的事件直接忽略（兜底防止与智能体互相触发刷屏）。
    /// </summary>
    public int MaxEventRunsPerMinute { get; set; } = 30;

    /// <summary>
    /// 模型步骤的单次调用超时。
    /// </summary>
    public TimeSpan LlmTimeout { get; set; } = TimeSpan.FromMinutes(3);

    public int LlmMaxOutputTokens { get; set; } = 8_000;

    /// <summary>
    /// 「遍历」步骤单次最多处理的列表项数，超出时该步骤失败。
    /// </summary>
    public int MaxLoopItems { get; set; } = 100;
}

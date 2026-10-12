namespace AiNative.Infrastructure.Agents;

public sealed class SchedulerOptions
{
    public const string SectionName = "Scheduler";

    /// <summary>
    /// 关闭后本进程不执行智能体（例如只承担 API 流量的实例）。
    /// </summary>
    public bool Enabled { get; set; } = true;

    public int MaxConcurrentRuns { get; set; } = 4;

    /// <summary>
    /// 没有信号时的兜底轮询间隔（暂停恢复、跨进程入队等情况靠它发现）。
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan TimerSweepInterval { get; set; } = TimeSpan.FromSeconds(30);
}

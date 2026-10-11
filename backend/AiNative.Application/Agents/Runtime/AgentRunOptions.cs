namespace AiNative.Application.Agents.Runtime;

public sealed class AgentRunOptions
{
    public const string SectionName = "Agents";

    /// <summary>
    /// 单轮心跳内最多与模型往返的次数。
    /// </summary>
    public int MaxStepsPerRun { get; set; } = 16;

    public int MaxOutputTokens { get; set; } = 16_000;

    /// <summary>
    /// 工作项领取锁的有效期，超过后可被其他执行接管（防止崩溃的执行永久占用任务）。
    /// </summary>
    public TimeSpan CheckoutLease { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 单轮最长执行时间，超时按 TimedOut 结束。
    /// </summary>
    public TimeSpan RunTimeout { get; set; } = TimeSpan.FromMinutes(20);
}

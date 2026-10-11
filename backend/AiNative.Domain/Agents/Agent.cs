using AiNative.Domain.Common;

namespace AiNative.Domain.Agents;

public enum AgentStatus
{
    Idle = 0,
    Running = 1,
    Paused = 2,
    Error = 3,
    Terminated = 4,
}

/// <summary>
/// 智能体员工：有岗位、上级、权限、模型配置和月度预算，按唤醒请求执行一轮"心跳"。
/// 组织关系（ReportsToId）决定它能把工作委派给谁。
/// </summary>
public sealed class Agent : TenantEntity
{
    public required string Name { get; set; }

    /// <summary>
    /// 岗位名称，例如"CEO"、"销售助理"、"财务分析师"。
    /// </summary>
    public required string Title { get; set; }

    /// <summary>
    /// 岗位说明与工作方式，作为系统提示词的一部分。
    /// </summary>
    public string Instructions { get; set; } = string.Empty;

    public Guid? ReportsToId { get; set; }

    public AgentStatus Status { get; set; } = AgentStatus.Idle;

    public string? PauseReason { get; set; }

    /// <summary>
    /// 模型网关中的提供方名称（appsettings Ai:Providers 下的键）。
    /// </summary>
    public required string ModelProvider { get; set; }

    public required string Model { get; set; }

    /// <summary>
    /// 授予智能体的功能权限键，语义与角色相同（支持 "tasks.*" 通配）。
    /// </summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>
    /// 额外要求人工审批的动作键；动作自身声明的审批策略之外再加一道闸。
    /// </summary>
    public List<string> ApprovalRequiredActions { get; set; } = [];

    /// <summary>
    /// 定时心跳间隔（秒），0 表示只在被唤醒时工作。
    /// </summary>
    public int HeartbeatIntervalSeconds { get; set; }

    public DateTimeOffset? LastHeartbeatAt { get; set; }

    /// <summary>
    /// 月度预算（美元），0 表示不限。超出后智能体自动暂停。
    /// </summary>
    public decimal MonthlyBudgetUsd { get; set; }

    public string? LastError { get; set; }

    public bool CanWork => Status is AgentStatus.Idle or AgentStatus.Running or AgentStatus.Error;

    public void Pause(string reason)
    {
        Status = AgentStatus.Paused;
        PauseReason = reason;
    }

    public void Resume()
    {
        if (Status == AgentStatus.Terminated)
        {
            throw new DomainException("agent.terminated", "已终止的智能体不能恢复。");
        }

        Status = AgentStatus.Idle;
        PauseReason = null;
    }

    public void AssignManager(Agent? manager, IReadOnlyDictionary<Guid, Guid?> reportingLines)
    {
        if (manager is null)
        {
            ReportsToId = null;
            return;
        }

        // 沿上级链向上走，出现自己即成环。
        Guid? cursor = manager.Id;
        var guard = 0;
        while (cursor is { } current)
        {
            if (current == Id)
            {
                throw new DomainException("agent.reporting_cycle", "汇报关系不能形成环。");
            }

            if (++guard > 1000 || !reportingLines.TryGetValue(current, out cursor))
            {
                break;
            }
        }

        ReportsToId = manager.Id;
    }
}

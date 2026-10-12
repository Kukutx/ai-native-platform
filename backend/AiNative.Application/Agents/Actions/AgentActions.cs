using System.ComponentModel;
using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Actions;
using AiNative.Contracts.Agents;
using AiNative.Domain.Agents;

namespace AiNative.Application.Agents.Actions;

public sealed record TeamMember(Guid Id, string Name, string Title, AgentStatus Status, Guid? ReportsToId, bool IsMe);

public sealed class ListTeamInput;

internal sealed class ListTeamAction(IRequestDispatcher dispatcher) : PlatformAction<ListTeamInput, IReadOnlyList<TeamMember>>
{
    public override string Key => "agents.list_team";

    public override string ModuleKey => AgentsModuleKeys.Agents;

    public override string Title => "查看团队";

    public override string Description => "列出租户里所有在岗的智能体员工（姓名、岗位、状态、汇报关系），用于判断该把工作委派给谁。";

    public override string RequiredFeature => Features.Agents.View;

    public override bool IsMutation => false;

    protected override async Task<IReadOnlyList<TeamMember>> ExecuteAsync(ListTeamInput input, ActionContext context, CancellationToken cancellationToken)
    {
        var agents = await dispatcher.SendAsync(new ListAgentsQuery(), cancellationToken).ConfigureAwait(false);
        return [.. agents
            .Where(a => a.Status != AgentStatus.Terminated)
            .Select(a => new TeamMember(a.Id, a.Name, a.Title, a.Status, a.ReportsToId, context.Actor.IsAgent && context.Actor.Id == a.Id))];
    }
}

public sealed class WakeAgentInput
{
    [Description("要唤醒的智能体 ID")]
    public required Guid AgentId { get; init; }

    [Description("唤醒原因，对方会在下一轮开始时看到")]
    public required string Reason { get; init; }

    [Description("相关任务 ID（可选）")]
    public Guid? TaskId { get; init; }
}

internal sealed class WakeAgentAction(IRequestDispatcher dispatcher) : PlatformAction<WakeAgentInput, WakeupDto>
{
    public override string Key => "agents.wake";

    public override string ModuleKey => AgentsModuleKeys.Agents;

    public override string Title => "唤醒智能体";

    public override string Description => "请另一个智能体立即开始一轮工作，例如请它查看某个任务的新进展。指派任务时会自动唤醒，不需要再调用。";

    public override string RequiredFeature => Features.Agents.Wake;

    protected override Task<WakeupDto> ExecuteAsync(WakeAgentInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(
            new WakeAgentCommand(input.AgentId, new WakeAgentRequest { Reason = input.Reason, WorkItemId = input.TaskId }),
            cancellationToken);
}

public sealed class HireAgentInput
{
    [Description("新智能体的名字")]
    public required string Name { get; init; }

    [Description("岗位名称，例如“市场研究员”")]
    public required string Title { get; init; }

    [Description("岗位职责与工作方式，会成为它的系统提示词")]
    public required string Instructions { get; init; }

    [Description("向谁汇报；为空时向发起者本人汇报")]
    public Guid? ReportsToId { get; init; }

    [Description("月度预算（美元），0 表示不限")]
    public decimal MonthlyBudgetUsd { get; init; }
}

internal sealed class HireAgentAction(IRequestDispatcher dispatcher) : PlatformAction<HireAgentInput, AgentDto>
{
    public override string Key => "agents.hire";

    public override string ModuleKey => AgentsModuleKeys.Agents;

    public override string Title => "招聘智能体";

    public override string Description => "新增一名智能体员工。由智能体发起时需要人工审批，批准后才会真正创建。";

    public override string RequiredFeature => Features.Agents.Manage;

    public override ApprovalPolicy ApprovalPolicy => ApprovalPolicy.RequiredForAgents;

    protected override Task<AgentDto> ExecuteAsync(HireAgentInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(
            new CreateAgentCommand(new SaveAgentRequest
            {
                Name = input.Name,
                Title = input.Title,
                Instructions = input.Instructions,
                ReportsToId = input.ReportsToId ?? (context.Actor.IsAgent ? context.Actor.Id : null),
                MonthlyBudgetUsd = input.MonthlyBudgetUsd,
            }),
            cancellationToken);
}

public static class AgentsModuleKeys
{
    public const string Agents = "agents";
}

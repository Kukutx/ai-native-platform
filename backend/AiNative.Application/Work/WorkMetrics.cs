using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Agents.Actions;
using AiNative.Application.Analytics;
using AiNative.Application.Projects;
using AiNative.Contracts.Analytics;
using AiNative.Domain.Work;

namespace AiNative.Application.Work;

internal static class WorkDimensions
{
    /// <summary>
    /// 按项目拆分会显示项目名称，所以还要求能看项目、项目管理模块已启用。
    /// </summary>
    public static readonly MetricDimension Project = new("project", "项目", ProjectFeatures.View, ProjectsModule.ModuleKey, "不属于项目");

    public static readonly MetricDimension Agent = new("agent", "负责的智能体", Features.Agents.View, EmptyLabel: "未指派智能体");

    public static readonly MetricDimension Priority = new("priority", "优先级");

    public static readonly IReadOnlyDictionary<string, string> PriorityNames = new Dictionary<string, string>
    {
        [nameof(WorkItemPriority.Low)] = "低",
        [nameof(WorkItemPriority.Medium)] = "中",
        [nameof(WorkItemPriority.High)] = "高",
        [nameof(WorkItemPriority.Urgent)] = "紧急",
    };

    public static Task<IReadOnlyDictionary<string, string>> LabelsAsync(MetricLabels labels, string dimension, IReadOnlyCollection<string> keys, CancellationToken cancellationToken) => dimension switch
    {
        "project" => labels.ProjectsAsync(keys, cancellationToken),
        "agent" => labels.AgentsAsync(keys, cancellationToken),
        _ => Task.FromResult(PriorityNames),
    };
}

internal sealed class TasksCreatedMetric(IAppDbContext db, MetricLabels labels) : PlatformMetric
{
    public override string Key => "tasks.created";

    public override string ModuleKey => AgentsModuleKeys.Agents;

    public override string Name => "新建任务";

    public override string Description => "按创建时间统计新建的任务数量（含子任务，人、智能体和工作流建的都算）。";

    public override string RequiredFeature => Features.Tasks.View;

    public override MetricUnit Unit => MetricUnit.Count;

    public override IReadOnlyList<MetricDimension> Dimensions { get; } = [WorkDimensions.Project, WorkDimensions.Agent, WorkDimensions.Priority];

    public override Task<IReadOnlyList<MetricSample>> SampleAsync(MetricScope scope, CancellationToken cancellationToken)
    {
        var tasks = db.WorkItems.AsQueryable().Where(t => t.CreatedAt >= scope.From && t.CreatedAt < scope.To);
        var rows = scope.Breakdown switch
        {
            "project" => tasks.Select(t => new MetricRow { At = t.CreatedAt, Group = t.ProjectId.ToString()!, Value = 1 }),
            "agent" => tasks.Select(t => new MetricRow { At = t.CreatedAt, Group = t.AssigneeAgentId.ToString()!, Value = 1 }),
            "priority" => tasks.Select(t => new MetricRow { At = t.CreatedAt, Group = t.Priority.ToString(), Value = 1 }),
            _ => tasks.Select(t => new MetricRow { At = t.CreatedAt, Value = 1 }),
        };
        return rows.ByDayAsync(scope.TimeZone, cancellationToken);
    }

    public override Task<IReadOnlyDictionary<string, string>> LabelsAsync(string dimension, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
        => WorkDimensions.LabelsAsync(labels, dimension, keys, cancellationToken);
}

internal sealed class TasksCompletedMetric(IAppDbContext db, MetricLabels labels) : PlatformMetric
{
    public override string Key => "tasks.completed";

    public override string ModuleKey => AgentsModuleKeys.Agents;

    public override string Name => "完成任务";

    public override string Description => "按完成时间统计状态为已完成的任务数量（含子任务），已取消的不算。";

    public override string RequiredFeature => Features.Tasks.View;

    public override MetricUnit Unit => MetricUnit.Count;

    public override IReadOnlyList<MetricDimension> Dimensions { get; } = [WorkDimensions.Project, WorkDimensions.Agent, WorkDimensions.Priority];

    public override Task<IReadOnlyList<MetricSample>> SampleAsync(MetricScope scope, CancellationToken cancellationToken)
    {
        var tasks = db.WorkItems.AsQueryable()
            .Where(t => t.Status == WorkItemStatus.Done && t.CompletedAt >= scope.From && t.CompletedAt < scope.To);
        var rows = scope.Breakdown switch
        {
            "project" => tasks.Select(t => new MetricRow { At = t.CompletedAt!.Value, Group = t.ProjectId.ToString()!, Value = 1 }),
            "agent" => tasks.Select(t => new MetricRow { At = t.CompletedAt!.Value, Group = t.AssigneeAgentId.ToString()!, Value = 1 }),
            "priority" => tasks.Select(t => new MetricRow { At = t.CompletedAt!.Value, Group = t.Priority.ToString(), Value = 1 }),
            _ => tasks.Select(t => new MetricRow { At = t.CompletedAt!.Value, Value = 1 }),
        };
        return rows.ByDayAsync(scope.TimeZone, cancellationToken);
    }

    public override Task<IReadOnlyDictionary<string, string>> LabelsAsync(string dimension, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
        => WorkDimensions.LabelsAsync(labels, dimension, keys, cancellationToken);
}

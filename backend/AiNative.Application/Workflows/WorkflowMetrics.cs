using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Analytics;
using AiNative.Contracts.Analytics;
using AiNative.Domain.Workflows;

namespace AiNative.Application.Workflows;

internal sealed class WorkflowRunsMetric(IAppDbContext db, MetricLabels labels) : PlatformMetric
{
    private static readonly IReadOnlyDictionary<string, string> StatusNames = new Dictionary<string, string>
    {
        [nameof(WorkflowRunStatus.Queued)] = "排队中",
        [nameof(WorkflowRunStatus.Running)] = "运行中",
        [nameof(WorkflowRunStatus.Waiting)] = "等待中",
        [nameof(WorkflowRunStatus.Succeeded)] = "成功",
        [nameof(WorkflowRunStatus.Failed)] = "失败",
        [nameof(WorkflowRunStatus.Cancelled)] = "已取消",
    };

    private static readonly IReadOnlyDictionary<string, string> TriggerNames = new Dictionary<string, string>
    {
        [nameof(WorkflowTriggerKind.Manual)] = "手动",
        [nameof(WorkflowTriggerKind.Event)] = "事件",
        [nameof(WorkflowTriggerKind.Schedule)] = "定时",
    };

    public override string Key => "workflows.runs";

    public override string ModuleKey => WorkflowsModule.ModuleKey;

    public override string Name => "工作流运行次数";

    public override string Description => "按开始时间统计自动化工作流的运行次数（手动、事件和定时触发的都算）。";

    public override string RequiredFeature => Features.Workflows.View;

    public override MetricUnit Unit => MetricUnit.Count;

    public override IReadOnlyList<MetricDimension> Dimensions { get; } =
    [
        new("workflow", "工作流"),
        new("status", "结果"),
        new("trigger", "触发方式"),
    ];

    public override Task<IReadOnlyList<MetricSample>> SampleAsync(MetricScope scope, CancellationToken cancellationToken)
    {
        var runs = db.WorkflowRuns.AsQueryable().Where(r => r.CreatedAt >= scope.From && r.CreatedAt < scope.To);
        var rows = scope.Breakdown switch
        {
            "workflow" => runs.Select(r => new MetricRow { At = r.CreatedAt, Group = r.WorkflowId.ToString(), Value = 1 }),
            "status" => runs.Select(r => new MetricRow { At = r.CreatedAt, Group = r.Status.ToString(), Value = 1 }),
            "trigger" => runs.Select(r => new MetricRow { At = r.CreatedAt, Group = r.TriggerKind.ToString(), Value = 1 }),
            _ => runs.Select(r => new MetricRow { At = r.CreatedAt, Value = 1 }),
        };
        return rows.ByDayAsync(scope.TimeZone, cancellationToken);
    }

    public override Task<IReadOnlyDictionary<string, string>> LabelsAsync(string dimension, IReadOnlyCollection<string> keys, CancellationToken cancellationToken) => dimension switch
    {
        "status" => Fixed(StatusNames),
        "trigger" => Fixed(TriggerNames),
        _ => labels.WorkflowsAsync(keys, cancellationToken),
    };
}

internal sealed class WorkflowSuccessRateMetric(IAppDbContext db, MetricLabels labels) : PlatformMetric
{
    public override string Key => "workflows.success_rate";

    public override string ModuleKey => WorkflowsModule.ModuleKey;

    public override string Name => "工作流成功率";

    public override string Description => "已结束的运行中成功的比例：成功 ÷（成功 + 失败），取消的不计入；按开始时间统计。";

    public override string RequiredFeature => Features.Workflows.View;

    public override MetricUnit Unit => MetricUnit.Percent;

    public override bool IsRatio => true;

    public override IReadOnlyList<MetricDimension> Dimensions { get; } = [new("workflow", "工作流")];

    public override Task<IReadOnlyList<MetricSample>> SampleAsync(MetricScope scope, CancellationToken cancellationToken)
    {
        var runs = db.WorkflowRuns.AsQueryable()
            .Where(r => (r.Status == WorkflowRunStatus.Succeeded || r.Status == WorkflowRunStatus.Failed) && r.CreatedAt >= scope.From && r.CreatedAt < scope.To);
        var rows = scope.Breakdown == "workflow"
            ? runs.Select(r => new MetricRow { At = r.CreatedAt, Group = r.WorkflowId.ToString(), Value = r.Status == WorkflowRunStatus.Succeeded ? 1 : 0, Weight = 1 })
            : runs.Select(r => new MetricRow { At = r.CreatedAt, Value = r.Status == WorkflowRunStatus.Succeeded ? 1 : 0, Weight = 1 });
        return rows.ByDayAsync(scope.TimeZone, cancellationToken);
    }

    public override Task<IReadOnlyDictionary<string, string>> LabelsAsync(string dimension, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
        => labels.WorkflowsAsync(keys, cancellationToken);
}

using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Agents.Actions;
using AiNative.Application.Analytics;
using AiNative.Application.Modules;
using AiNative.Contracts.Analytics;
using AiNative.Domain.Agents;
using AiNative.Domain.Common;

namespace AiNative.Application.Agents;

internal static class RunDimensions
{
    public static readonly MetricDimension Agent = new("agent", "智能体", Features.Agents.View);

    public static readonly IReadOnlyDictionary<string, string> StatusNames = new Dictionary<string, string>
    {
        [nameof(RunStatus.Queued)] = "排队中",
        [nameof(RunStatus.Running)] = "执行中",
        [nameof(RunStatus.Succeeded)] = "成功",
        [nameof(RunStatus.Failed)] = "失败",
        [nameof(RunStatus.Cancelled)] = "已取消",
        [nameof(RunStatus.TimedOut)] = "超时",
    };
}

internal sealed class AgentRunsMetric(IAppDbContext db, MetricLabels labels) : PlatformMetric
{
    public override string Key => "agents.runs";

    public override string ModuleKey => AgentsModuleKeys.Agents;

    public override string Name => "智能体执行次数";

    public override string Description => "按开始排队的时间统计智能体的执行（每次被唤醒处理工作算一次）。";

    public override string RequiredFeature => Features.Runs.View;

    public override MetricUnit Unit => MetricUnit.Count;

    public override IReadOnlyList<MetricDimension> Dimensions { get; } = [RunDimensions.Agent, new("status", "结果")];

    public override Task<IReadOnlyList<MetricSample>> SampleAsync(MetricScope scope, CancellationToken cancellationToken)
    {
        var runs = db.HeartbeatRuns.AsQueryable().Where(r => r.CreatedAt >= scope.From && r.CreatedAt < scope.To);
        var rows = scope.Breakdown switch
        {
            "agent" => runs.Select(r => new MetricRow { At = r.CreatedAt, Group = r.AgentId.ToString(), Value = 1 }),
            "status" => runs.Select(r => new MetricRow { At = r.CreatedAt, Group = r.Status.ToString(), Value = 1 }),
            _ => runs.Select(r => new MetricRow { At = r.CreatedAt, Value = 1 }),
        };
        return rows.ByDayAsync(scope.TimeZone, cancellationToken);
    }

    public override Task<IReadOnlyDictionary<string, string>> LabelsAsync(string dimension, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
        => dimension == "status" ? Fixed(RunDimensions.StatusNames) : labels.AgentsAsync(keys, cancellationToken);
}

internal sealed class AgentRunSuccessRateMetric(IAppDbContext db, MetricLabels labels) : PlatformMetric
{
    public override string Key => "agents.run_success_rate";

    public override string ModuleKey => AgentsModuleKeys.Agents;

    public override string Name => "执行成功率";

    public override string Description => "已结束的执行中成功的比例：成功 ÷（成功 + 失败 + 超时），手动取消的不计入；按开始排队的时间统计。";

    public override string RequiredFeature => Features.Runs.View;

    public override MetricUnit Unit => MetricUnit.Percent;

    public override bool IsRatio => true;

    public override IReadOnlyList<MetricDimension> Dimensions { get; } = [RunDimensions.Agent];

    public override Task<IReadOnlyList<MetricSample>> SampleAsync(MetricScope scope, CancellationToken cancellationToken)
    {
        var runs = db.HeartbeatRuns.AsQueryable()
            .Where(r => (r.Status == RunStatus.Succeeded || r.Status == RunStatus.Failed || r.Status == RunStatus.TimedOut)
                        && r.CreatedAt >= scope.From && r.CreatedAt < scope.To);
        var rows = scope.Breakdown == "agent"
            ? runs.Select(r => new MetricRow { At = r.CreatedAt, Group = r.AgentId.ToString(), Value = r.Status == RunStatus.Succeeded ? 1 : 0, Weight = 1 })
            : runs.Select(r => new MetricRow { At = r.CreatedAt, Value = r.Status == RunStatus.Succeeded ? 1 : 0, Weight = 1 });
        return rows.ByDayAsync(scope.TimeZone, cancellationToken);
    }

    public override Task<IReadOnlyDictionary<string, string>> LabelsAsync(string dimension, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
        => labels.AgentsAsync(keys, cancellationToken);
}

internal static class UsageDimensions
{
    public static readonly IReadOnlyList<MetricDimension> All =
    [
        new("actor", "使用者", EmptyLabel: "系统"),
        new("model", "模型"),
        new("source", "来源"),
    ];

    public static readonly IReadOnlyDictionary<string, string> SourceNames = new Dictionary<string, string>
    {
        ["run"] = "智能体执行",
        ["chat"] = "对话",
        ["other"] = "其他（工作流、索引等）",
    };

    public static Task<IReadOnlyDictionary<string, string>> LabelsAsync(MetricLabels labels, string dimension, IReadOnlyCollection<string> keys, CancellationToken cancellationToken) => dimension switch
    {
        "actor" => labels.ActorsAsync(keys, cancellationToken),
        "source" => Task.FromResult(SourceNames),
        _ => Task.FromResult<IReadOnlyDictionary<string, string>>(keys.ToDictionary(k => k, k => k)),
    };
}

internal sealed class AiCostMetric(IAppDbContext db, MetricLabels labels) : PlatformMetric
{
    public override string Key => "ai.cost";

    public override string ModuleKey => PlatformCoreModule.ModuleKey;

    public override string Name => "AI 花费";

    public override string Description => "按发生时间统计调用模型的花费（美元，按价格表估算），包括智能体执行、对话和工作流里的模型步骤。";

    public override string RequiredFeature => Features.Platform.UsageView;

    public override MetricUnit Unit => MetricUnit.Usd;

    public override IReadOnlyList<MetricDimension> Dimensions => UsageDimensions.All;

    public override Task<IReadOnlyList<MetricSample>> SampleAsync(MetricScope scope, CancellationToken cancellationToken)
    {
        var events = db.CostEvents.AsQueryable().Where(c => c.OccurredAt >= scope.From && c.OccurredAt < scope.To);
        var rows = scope.Breakdown switch
        {
            "actor" => events.Select(c => new MetricRow { At = c.OccurredAt, Group = c.Actor.Kind == ActorKind.System ? string.Empty : c.Actor.Id.ToString()!, Value = c.CostUsd }),
            "model" => events.Select(c => new MetricRow { At = c.OccurredAt, Group = c.Provider + "/" + c.Model, Value = c.CostUsd }),
            "source" => events.Select(c => new MetricRow { At = c.OccurredAt, Group = c.RunId != null ? "run" : c.ConversationId != null ? "chat" : "other", Value = c.CostUsd }),
            _ => events.Select(c => new MetricRow { At = c.OccurredAt, Value = c.CostUsd }),
        };
        return rows.ByDayAsync(scope.TimeZone, cancellationToken);
    }

    public override Task<IReadOnlyDictionary<string, string>> LabelsAsync(string dimension, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
        => UsageDimensions.LabelsAsync(labels, dimension, keys, cancellationToken);
}

internal sealed class AiTokensMetric(IAppDbContext db, MetricLabels labels) : PlatformMetric
{
    public override string Key => "ai.tokens";

    public override string ModuleKey => PlatformCoreModule.ModuleKey;

    public override string Name => "令牌用量";

    public override string Description => "按发生时间统计模型的输入与输出令牌（token）总数。";

    public override string RequiredFeature => Features.Platform.UsageView;

    public override MetricUnit Unit => MetricUnit.Tokens;

    public override IReadOnlyList<MetricDimension> Dimensions => UsageDimensions.All;

    public override Task<IReadOnlyList<MetricSample>> SampleAsync(MetricScope scope, CancellationToken cancellationToken)
    {
        var events = db.CostEvents.AsQueryable().Where(c => c.OccurredAt >= scope.From && c.OccurredAt < scope.To);
        var rows = scope.Breakdown switch
        {
            "actor" => events.Select(c => new MetricRow { At = c.OccurredAt, Group = c.Actor.Kind == ActorKind.System ? string.Empty : c.Actor.Id.ToString()!, Value = c.InputTokens + c.OutputTokens }),
            "model" => events.Select(c => new MetricRow { At = c.OccurredAt, Group = c.Provider + "/" + c.Model, Value = c.InputTokens + c.OutputTokens }),
            "source" => events.Select(c => new MetricRow { At = c.OccurredAt, Group = c.RunId != null ? "run" : c.ConversationId != null ? "chat" : "other", Value = c.InputTokens + c.OutputTokens }),
            _ => events.Select(c => new MetricRow { At = c.OccurredAt, Value = c.InputTokens + c.OutputTokens }),
        };
        return rows.ByDayAsync(scope.TimeZone, cancellationToken);
    }

    public override Task<IReadOnlyDictionary<string, string>> LabelsAsync(string dimension, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
        => UsageDimensions.LabelsAsync(labels, dimension, keys, cancellationToken);
}

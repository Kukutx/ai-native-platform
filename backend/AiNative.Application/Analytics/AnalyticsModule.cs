using AiNative.Application.Access;
using AiNative.Application.Modules;

namespace AiNative.Application.Analytics;

public static class AnalyticsFeatures
{
    public const string View = "analytics.view";
    public const string Manage = "analytics.manage";
}

/// <summary>
/// 经营分析推送给客户端的实体变化类型。
/// </summary>
public static class AnalyticsRealtime
{
    public const string Report = "analytics_report";
}

/// <summary>
/// 经营分析：各模块注册自己的指标（<see cref="IPlatformMetric"/>），这里统一按时区分天汇总、拆分、对比，
/// 并提供概览和可保存的报表。每个指标仍要求查看者有它所属模块的查看权限，分析权限本身不会放大可见范围。
/// </summary>
internal sealed class AnalyticsModule : IPlatformModule
{
    public const string ModuleKey = "analytics";

    public string Key => ModuleKey;

    public string Name => "经营分析";

    public string Description => "销售、进销存、任务、智能体与自动化的指标趋势、拆分与环比，经营概览和可保存的报表；对话里也可以直接问数据。";

    public bool IsCore => false;

    public bool EnabledByDefault => true;

    public IReadOnlyList<FeatureDefinition> Features { get; } =
    [
        new(AnalyticsFeatures.View, "查看经营分析（各指标另需所属模块的查看权限）", ModuleKey),
        new(AnalyticsFeatures.Manage, "新建和管理共享报表", ModuleKey),
    ];
}

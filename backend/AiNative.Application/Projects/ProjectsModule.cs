using AiNative.Application.Access;
using AiNative.Application.Agents.Actions;
using AiNative.Application.Modules;

namespace AiNative.Application.Projects;

public static class ProjectFeatures
{
    public const string View = "projects.view";
    public const string Manage = "projects.manage";
}

public static class ProjectEventKeys
{
    public const string ProjectCreated = "projects.project_created";
    public const string ProjectStatusChanged = "projects.project_status_changed";
    public const string MilestoneCompleted = "projects.milestone_completed";
}

/// <summary>
/// 项目管理推送给客户端的实体变化类型。
/// </summary>
public static class ProjectRealtime
{
    public const string Project = "project";
}

/// <summary>
/// 项目管理：把任务按项目和里程碑组织起来，实时统计进度。任务本身属于智能体团队模块，所以依赖它。
/// </summary>
internal sealed class ProjectsModule : IPlatformModule
{
    public const string ModuleKey = "projects";

    public string Key => ModuleKey;

    public string Name => "项目管理";

    public string Description => "项目、里程碑与成员：任务归入项目和里程碑，进度、阻塞和逾期实时统计；甘特图按任务的起止日期和前后依赖排期，标出冲突和关键路径；负责人可以是智能体，项目里的任务被阻塞或里程碑的任务全部完成时会唤醒它。";

    public bool IsCore => false;

    public bool EnabledByDefault => true;

    public IReadOnlyList<string> DependsOn { get; } = [AgentsModuleKeys.Agents];

    public IReadOnlyList<FeatureDefinition> Features { get; } =
    [
        new(ProjectFeatures.View, "查看项目、里程碑、成员与进度", ModuleKey),
        new(ProjectFeatures.Manage, "新建和管理项目、里程碑与成员", ModuleKey),
    ];

    public IReadOnlyList<PlatformEventDefinition> Events { get; } =
    [
        new(
            ProjectEventKeys.ProjectCreated,
            "新项目",
            ModuleKey,
            ProjectFeatures.View,
            "新建项目时触发。载荷：trigger.project 为项目（id、name、description、status、lead：kind、id、name，startDate、targetDate），trigger.actor 为创建者（kind、id、name）。"),
        new(
            ProjectEventKeys.ProjectStatusChanged,
            "项目状态变化",
            ModuleKey,
            ProjectFeatures.View,
            "项目状态改变时触发。载荷：trigger.project 为变化后的项目（含 progress：total、done、inProgress、blocked、overdue、percent），trigger.from / trigger.to 为变化前后的状态（planned 规划中、active 进行中、on_hold 暂停、completed 已完成、cancelled 已取消），trigger.actor 为操作者。只关心完成可以用过滤条件 trigger.to == 'completed'。"),
        new(
            ProjectEventKeys.MilestoneCompleted,
            "里程碑完成",
            ModuleKey,
            ProjectFeatures.View,
            "里程碑被标记完成时触发。载荷：trigger.project 为所属项目，trigger.milestone 为里程碑（id、name、description、dueDate、progress），trigger.actor 为操作者。"),
    ];
}

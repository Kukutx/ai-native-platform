using AiNative.Application.Access;
using AiNative.Application.Analytics;
using AiNative.Application.Crm;
using AiNative.Application.Erp;
using AiNative.Application.Integrations;
using AiNative.Application.Projects;

namespace AiNative.Application.Platform;

/// <summary>
/// 新租户的内置角色。之后租户可以随意修改权限或新增角色。
/// </summary>
public static class DefaultRoles
{
    public const string Admin = "admin";
    public const string Member = "member";
    public const string Viewer = "viewer";

    public static IReadOnlyList<(string Key, string Name, string Description, string[] Features)> All { get; } =
    [
        (Admin, "管理员", "拥有全部权限", [FeatureMatcher.All]),
        (Member, "成员", "管理任务、项目、知识库、客户、销售订单、报表与自动化工作流，与智能体协作，使用工作台对话和外部工具",
        [
            Features.Agents.View, Features.Agents.Wake, "tasks.*", Features.Runs.View, Features.Approvals.View,
            Features.Chat.Use, Features.Platform.MembersView, "knowledge.*", Features.Memory.Use, Features.Platform.ApiKeysUse,
            "workflows.*", CrmFeatures.View, CrmFeatures.Manage, ErpFeatures.View, ErpFeatures.Sales,
            ProjectFeatures.View, ProjectFeatures.Manage, AnalyticsFeatures.View, AnalyticsFeatures.Manage,
            IntegrationFeatures.View, IntegrationFeatures.ToolsUse,
        ]),
        (Viewer, "只读", "只能查看",
        [
            Features.Agents.View, Features.Tasks.View, Features.Runs.View, Features.Approvals.View, Features.Platform.MembersView,
            Features.Knowledge.View, Features.Workflows.View, CrmFeatures.View, ErpFeatures.View, ProjectFeatures.View,
            AnalyticsFeatures.View, IntegrationFeatures.View,
        ]),
    ];
}

using AiNative.Application.Access;
using AiNative.Application.Crm;

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
        (Member, "成员", "管理任务、知识库、客户与自动化工作流，与智能体协作，使用工作台对话",
        [
            Features.Agents.View, Features.Agents.Wake, "tasks.*", Features.Runs.View, Features.Approvals.View,
            Features.Chat.Use, Features.Platform.MembersView, "knowledge.*", Features.Memory.Use, Features.Platform.ApiKeysUse,
            "workflows.*", CrmFeatures.View, CrmFeatures.Manage,
        ]),
        (Viewer, "只读", "只能查看",
        [
            Features.Agents.View, Features.Tasks.View, Features.Runs.View, Features.Approvals.View, Features.Platform.MembersView,
            Features.Knowledge.View, Features.Workflows.View, CrmFeatures.View,
        ]),
    ];
}

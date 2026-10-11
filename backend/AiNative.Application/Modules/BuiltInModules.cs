using AiNative.Application.Access;
using AiNative.Application.Agents.Actions;

namespace AiNative.Application.Modules;

/// <summary>
/// 平台核心：租户、组织、成员、角色、模块与用量。不可停用。
/// </summary>
internal sealed class PlatformCoreModule : IPlatformModule
{
    public const string ModuleKey = "platform";

    public string Key => ModuleKey;

    public string Name => "平台核心";

    public string Description => "租户、组织架构、成员、角色权限、模块与用量管理。";

    public bool IsCore => true;

    public bool EnabledByDefault => true;

    public IReadOnlyList<FeatureDefinition> Features { get; } =
    [
        new(Access.Features.Platform.MembersView, "查看成员", ModuleKey),
        new(Access.Features.Platform.MembersManage, "管理成员", ModuleKey),
        new(Access.Features.Platform.RolesManage, "管理角色", ModuleKey),
        new(Access.Features.Platform.OrganizationsManage, "管理组织架构", ModuleKey),
        new(Access.Features.Platform.ModulesManage, "启用/停用模块", ModuleKey),
        new(Access.Features.Platform.UsageView, "查看用量与费用", ModuleKey),
        new(Access.Features.Platform.ApiKeysUse, "创建个人 API 密钥（MCP 与外部集成）", ModuleKey),
    ];
}

/// <summary>
/// 智能体员工：组织架构、任务协作、心跳执行、预算与审批。
/// </summary>
internal sealed class AgentsModule : IPlatformModule
{
    public string Key => AgentsModuleKeys.Agents;

    public string Name => "智能体团队";

    public string Description => "像组建公司一样组建智能体团队：岗位与汇报关系、任务指派与原子领取、按心跳自主执行、预算控制与人工审批。";

    public bool IsCore => false;

    public bool EnabledByDefault => true;

    public IReadOnlyList<FeatureDefinition> Features { get; } =
    [
        new(Access.Features.Agents.View, "查看智能体", AgentsModuleKeys.Agents),
        new(Access.Features.Agents.Manage, "管理智能体（招聘、调整、暂停）", AgentsModuleKeys.Agents),
        new(Access.Features.Agents.Wake, "唤醒智能体", AgentsModuleKeys.Agents),
        new(Access.Features.Tasks.View, "查看任务", AgentsModuleKeys.Agents),
        new(Access.Features.Tasks.Manage, "管理任务", AgentsModuleKeys.Agents),
        new(Access.Features.Tasks.Comment, "评论任务", AgentsModuleKeys.Agents),
        new(Access.Features.Runs.View, "查看执行记录", AgentsModuleKeys.Agents),
        new(Access.Features.Approvals.View, "查看审批", AgentsModuleKeys.Agents),
        new(Access.Features.Approvals.Decide, "处理审批", AgentsModuleKeys.Agents),
    ];
}

/// <summary>
/// 工作台对话：自然语言操作整个平台。
/// </summary>
internal sealed class ChatModule : IPlatformModule
{
    public const string ModuleKey = "chat";

    public string Key => ModuleKey;

    public string Name => "工作台对话";

    public string Description => "像 ChatGPT 一样对话，但能直接查询和操作平台数据，也可以和任一智能体员工单独聊。";

    public bool IsCore => false;

    public bool EnabledByDefault => true;

    public IReadOnlyList<FeatureDefinition> Features { get; } =
    [
        new(Access.Features.Chat.Use, "使用工作台对话", ModuleKey),
    ];
}

/// <summary>
/// 知识与记忆：知识库文档的混合检索（向量 + 关键词），以及人和智能体的长期记忆。
/// </summary>
internal sealed class KnowledgeModule : IPlatformModule
{
    public const string ModuleKey = "knowledge";

    public string Key => ModuleKey;

    public string Name => "知识与记忆";

    public string Description => "把文档放进知识库，人、智能体和外部 MCP 客户端都能检索；智能体会记住工作中学到的事实与约定，下次被唤醒时自动回忆。";

    public bool IsCore => false;

    public bool EnabledByDefault => true;

    public IReadOnlyList<FeatureDefinition> Features { get; } =
    [
        new(Access.Features.Knowledge.View, "检索与查看知识库", ModuleKey),
        new(Access.Features.Knowledge.Manage, "管理知识库文档", ModuleKey),
        new(Access.Features.Memory.Use, "使用长期记忆（记住与回忆）", ModuleKey),
        new(Access.Features.Memory.Manage, "管理全部记忆（含智能体与其他成员的私有记忆）", ModuleKey),
    ];
}

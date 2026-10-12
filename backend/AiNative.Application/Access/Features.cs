namespace AiNative.Application.Access;

/// <summary>
/// 功能权限定义。由模块声明，角色在数据库里自由组合这些键。
/// </summary>
public sealed record FeatureDefinition(string Key, string Name, string ModuleKey);

/// <summary>
/// 内置模块的功能权限键。新模块在自己的描述里声明，不需要改这里。
/// </summary>
public static class Features
{
    public static class Platform
    {
        public const string MembersView = "platform.members.view";
        public const string MembersManage = "platform.members.manage";
        public const string RolesManage = "platform.roles.manage";
        public const string ModulesManage = "platform.modules.manage";
        public const string OrganizationsManage = "platform.organizations.manage";
        public const string UsageView = "platform.usage.view";
        public const string ApiKeysUse = "platform.api_keys.use";
        public const string SettingsManage = "platform.settings.manage";
    }

    public static class Agents
    {
        public const string View = "agents.view";
        public const string Manage = "agents.manage";
        public const string Wake = "agents.wake";
    }

    public static class Tasks
    {
        public const string View = "tasks.view";
        public const string Manage = "tasks.manage";
        public const string Comment = "tasks.comment";
    }

    public static class Runs
    {
        public const string View = "runs.view";
    }

    public static class Approvals
    {
        public const string View = "approvals.view";
        public const string Decide = "approvals.decide";
    }

    public static class Chat
    {
        public const string Use = "chat.use";
    }

    public static class Knowledge
    {
        public const string View = "knowledge.view";
        public const string Manage = "knowledge.manage";
    }

    public static class Memory
    {
        public const string Use = "memory.use";
        public const string Manage = "memory.manage";
    }

    public static class Workflows
    {
        public const string View = "workflows.view";
        public const string Manage = "workflows.manage";
        public const string Run = "workflows.run";
    }
}

using AiNative.Application.Access;
using AiNative.Application.Modules;

namespace AiNative.Application.Integrations;

public static class IntegrationFeatures
{
    public const string View = "integrations.view";
    public const string Manage = "integrations.manage";

    /// <summary>
    /// 调用外部 MCP 服务提供的工具（智能体、对话、工作流里）。
    /// </summary>
    public const string ToolsUse = "integrations.tools.use";

    /// <summary>
    /// 在工作流 HTTP 请求和外部服务的请求头里引用密钥。能引用就能把密钥发往任意地址，等同于能读取，所以单独授权。
    /// </summary>
    public const string SecretsUse = "integrations.secrets.use";
}

/// <summary>
/// 集成推送给客户端的实体变化类型。
/// </summary>
public static class IntegrationRealtime
{
    public const string McpServer = "mcp_server";
    public const string Secret = "tenant_secret";
}

/// <summary>
/// 集成：接入外部 MCP 服务，把它们的工具变成平台动作（智能体、对话、工作流共用，权限与审批统一检查）；
/// 加密保存外部服务的令牌，在请求头里引用，不以明文出现在任何配置和记录里。
/// </summary>
internal sealed class IntegrationsModule : IPlatformModule
{
    public const string ModuleKey = "integrations";

    public string Key => ModuleKey;

    public string Name => "集成";

    public string Description => "接入外部 MCP 服务（GitHub、数据库、搜索等），智能体、对话和工作流都能调用它们的工具；外部服务的令牌加密保存，只在发请求时使用。";

    public bool IsCore => false;

    public bool EnabledByDefault => true;

    public IReadOnlyList<FeatureDefinition> Features { get; } =
    [
        new(IntegrationFeatures.View, "查看接入的外部服务和密钥名称", ModuleKey),
        new(IntegrationFeatures.Manage, "管理外部 MCP 服务和密钥", ModuleKey),
        new(IntegrationFeatures.ToolsUse, "调用外部 MCP 服务提供的工具", ModuleKey),
        new(IntegrationFeatures.SecretsUse, "在工作流 HTTP 请求和外部服务的请求头里引用密钥", ModuleKey),
    ];
}

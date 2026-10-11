using System.Text.Json;
using AiNative.Application.Actions;
using AiNative.Application.Common;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AiNative.Api.Mcp;

/// <summary>
/// MCP 服务：把当前身份可用的平台动作原样开放为 MCP 工具（Streamable HTTP，无状态，挂在 /mcp）。
/// 鉴权与 REST 接口相同（登录令牌或个人 API 密钥），工具列表和每次调用都经过同一个动作入口，
/// 权限、模块启用和审批规则与界面、智能体完全一致。
/// </summary>
internal static class PlatformMcpServer
{
    public const string Route = "/mcp";

    private const string Instructions = """
        这是 AI Native 平台的 MCP 服务。工具对应平台里的业务动作：查询和管理任务、与智能体团队协作、检索知识库、读写长期记忆等。
        工具以 API 密钥所属成员的身份和权限执行，结果实时同步到平台的 Web、桌面和移动端。
        工具结果是 JSON：status 为 ok 时 output 是数据；为 pending_approval 时表示已提交审批，不要重复调用。
        """;

    public static IServiceCollection AddPlatformMcp(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "ai-native-platform", Title = "AI Native 平台", Version = "1.0.0" };
                options.ServerInstructions = Instructions;
            })
            .WithHttpTransport(options => options.Stateless = true)
            .WithListToolsHandler(ListToolsAsync)
            .WithCallToolHandler(CallToolAsync);
        return services;
    }

    private static async ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> context, CancellationToken cancellationToken)
    {
        var invoker = RequestServices(context).GetRequiredService<IActionInvoker>();
        var actions = await invoker.GetAvailableAsync(cancellationToken).ConfigureAwait(false);
        return new ListToolsResult
        {
            Tools = [.. actions.Select(action => new Tool
            {
                Name = ActionToolset.ToToolName(action.Key),
                Title = action.Title,
                Description = action.Description,
                InputSchema = invoker.GetInputSchema(action),
                Annotations = new ToolAnnotations
                {
                    Title = action.Title,
                    ReadOnlyHint = !action.IsMutation,
                    OpenWorldHint = false,
                },
            })],
        };
    }

    private static async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        var invoker = RequestServices(context).GetRequiredService<IActionInvoker>();
        var name = context.Params?.Name ?? string.Empty;
        var action = invoker.Actions.FirstOrDefault(a => ActionToolset.ToToolName(a.Key) == name);
        ActionInvocationResult result;
        if (action is null)
        {
            result = new ActionInvocationResult(ActionInvocationStatus.NotFound, Error: $"没有名为 {name} 的工具。");
        }
        else
        {
            var arguments = context.Params?.Arguments is { } args
                ? JsonSerializer.SerializeToElement(args, PlatformJson.Options)
                : JsonSerializer.SerializeToElement(new { });
            result = await invoker.InvokeAsync(action.Key, arguments, new ActionInvocationOptions { Rationale = "通过 MCP 调用" }, cancellationToken)
                .ConfigureAwait(false);
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = ActionToolset.FormatForModel(result) }],
            IsError = result.Status is not (ActionInvocationStatus.Succeeded or ActionInvocationStatus.PendingApproval),
        };
    }

    /// <summary>
    /// 本次 HTTP 请求的服务容器：认证中间件已在其中填好租户上下文（身份、权限）。
    /// 无状态模式下处理器运行在对应 HTTP 请求的执行上下文里，可以通过 IHttpContextAccessor 取到。
    /// </summary>
    private static IServiceProvider RequestServices<T>(RequestContext<T> context)
        => context.Services?.GetService<IHttpContextAccessor>()?.HttpContext?.RequestServices
           ?? throw new InvalidOperationException("MCP 请求不在 HTTP 上下文中。");
}

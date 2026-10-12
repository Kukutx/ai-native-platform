using System.Runtime.CompilerServices;
using AiNative.Api.Security;
using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Chat;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Chat;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Api.Realtime;

/// <summary>
/// 工作台实时通道：
/// - 连接自动加入所在空间的分组，接收实体变化通知（客户端据此刷新缓存）；
/// - 订阅某次运行，接收智能体执行过程的逐字输出与工具调用；
/// - 流式对话。
/// 每次调用都由 <see cref="TenantHubFilter"/> 重新写入租户上下文。
/// </summary>
public sealed class RealtimeHub(ITenantContext tenant, IAppDbContext db, IChatService chat) : Hub
{
    public static string TenantGroup(Guid tenantId) => $"tenant:{tenantId:N}";

    public static string RunGroup(Guid runId) => $"run:{runId:N}";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup(tenant.TenantId)).ConfigureAwait(false);
        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public async Task SubscribeRun(Guid runId)
    {
        tenant.Demand(Features.Runs.View);

        // 查询受租户过滤保护：别的空间的运行在这里查不到。
        if (!await db.HeartbeatRuns.AnyAsync(r => r.Id == runId, Context.ConnectionAborted).ConfigureAwait(false))
        {
            throw new HubException("运行不存在。");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, RunGroup(runId)).ConfigureAwait(false);
    }

    public Task UnsubscribeRun(Guid runId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, RunGroup(runId));

    public async IAsyncEnumerable<ChatStreamEvent> StreamChat(
        Guid conversationId,
        string text,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var evt in chat.SendAsync(conversationId, text, cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }
    }
}

/// <summary>
/// SignalR 每次调用使用独立作用域，这里按连接上的令牌身份重新解析权限并写入租户上下文；
/// 成员被移除或修改了密码后，已建立的连接也会在下一次调用时被拒绝。
/// </summary>
internal sealed class TenantHubFilter : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        await EnterAsync(invocationContext.Context, invocationContext.ServiceProvider).ConfigureAwait(false);
        return await next(invocationContext).ConfigureAwait(false);
    }

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        await EnterAsync(context.Context, context.ServiceProvider).ConfigureAwait(false);
        await next(context).ConfigureAwait(false);
    }

    private static async Task EnterAsync(HubCallerContext caller, IServiceProvider services)
    {
        if (PlatformPrincipal.From(caller.User) is not { } principal)
        {
            throw new HubException("未登录。");
        }

        if (DenyApiKeyAttribute.IsApiKey(caller.User))
        {
            throw new HubException("实时通道不接受 API 密钥，请使用登录令牌。");
        }

        var access = await services.GetRequiredService<Application.Platform.IAccessResolver>()
            .ResolveAsync(principal.UserId, principal.TenantId, principal.MembershipId, caller.ConnectionAborted)
            .ConfigureAwait(false) ?? throw new HubException("成员身份已失效。");
        if (Application.Platform.PlatformClaims.ReadTokenVersion(caller.User?.FindFirst(Application.Platform.PlatformClaims.TokenVersion)?.Value) != access.TokenVersion)
        {
            throw new HubException("登录已失效，请重新登录。");
        }
        services.GetRequiredService<TenantContext>()
            .Enter(access.TenantId, Domain.Common.Actor.ForUser(access.UserId), access.Features, access.MembershipId);
    }
}

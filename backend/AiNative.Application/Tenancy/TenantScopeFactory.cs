using AiNative.Domain.Common;
using Microsoft.Extensions.DependencyInjection;

namespace AiNative.Application.Tenancy;

/// <summary>
/// 在独立的依赖注入作用域里以指定身份执行一段工作。后台执行、实时通道和工具调用都用它，
/// 每次工具调用拥有自己的数据库上下文，一个动作失败不会污染调用方正在跟踪的实体。
/// </summary>
public interface ITenantScopeFactory
{
    Task<T> RunAsync<T>(Guid tenantId, Actor actor, IEnumerable<string> features, Func<IServiceProvider, Task<T>> work);
}

internal sealed class TenantScopeFactory(IServiceScopeFactory scopes) : ITenantScopeFactory
{
    public async Task<T> RunAsync<T>(Guid tenantId, Actor actor, IEnumerable<string> features, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Enter(tenantId, actor, features);
        return await work(scope.ServiceProvider).ConfigureAwait(false);
    }
}

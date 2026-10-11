using System.Net.Http.Headers;
using System.Net.Http.Json;
using AiNative.Application.Common;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Auth;
using AiNative.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiNative.IntegrationTests.Infrastructure;

/// <summary>
/// 每个测试一个全新数据库和一个 API 宿主（含调度器），测试之间完全隔离。
/// </summary>
public abstract class IntegrationTest(PostgresFixture postgres) : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    protected ApiFactory Factory { get; private set; } = null!;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync();
        Factory = new ApiFactory(_connectionString);
        _ = Factory.Server;
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await postgres.DropDatabaseAsync(_connectionString);
        GC.SuppressFinalize(this);
    }

    protected HttpClient Client(string? token = null)
    {
        var client = Factory.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    protected async Task<AuthResponse> RegisterAsync(string email = "owner@example.com", string tenantName = "测试空间")
    {
        using var client = Client();
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            Email = email,
            Password = "Passw0rd!x",
            DisplayName = email.Split('@')[0],
            TenantName = tenantName,
        }, PlatformJson.Options, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>(PlatformJson.Options, Ct))!;
    }

    protected static async Task<T> GetAsync<T>(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative), Ct);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(PlatformJson.Options, Ct))!;
    }

    protected static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, PlatformJson.Options, Ct);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(PlatformJson.Options, Ct))!;
    }

    protected static async Task<T> PutAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PutAsJsonAsync(url, body, PlatformJson.Options, Ct);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(PlatformJson.Options, Ct))!;
    }

    protected static async Task<AgentDto> StarterAgentAsync(HttpClient client)
        => (await GetAsync<List<AgentDto>>(client, "/api/agents")).Single();

    protected async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>
    /// 轮询直到条件满足（智能体在后台异步执行）。
    /// </summary>
    protected static async Task<T> EventuallyAsync<T>(Func<Task<T>> probe, Func<T, bool> done, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (true)
        {
            var value = await probe();
            if (done(value))
            {
                return value;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition not met in time. Last value: {PlatformJson.Serialize(value)}");
            }

            await Task.Delay(100, Ct);
        }
    }

    protected Task<List<AiNative.Domain.Agents.HeartbeatRun>> RunsAsync()
        => WithDbAsync(db => db.HeartbeatRuns.IgnoreQueryFilters().AsNoTracking().OrderBy(r => r.CreatedAt).ToListAsync(Ct));

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            throw new HttpRequestException($"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");
        }
    }
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiNative.Application.Common;
using AiNative.Contracts.Actions;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Auth;
using AiNative.Contracts.Platform;
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

    /// <summary>
    /// 本测试类的宿主额外配置。
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>();

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync();
        Factory = new ApiFactory(_connectionString, Settings);
        _ = Factory.Server;
    }

    public virtual async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await postgres.DropDatabaseAsync(_connectionString);
        GC.SuppressFinalize(this);
    }

    protected HttpClient Client(string? token = null) => ClientOf(Factory, token);

    protected static HttpClient ClientOf(ApiFactory factory, string? token = null)
    {
        var client = factory.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    /// <summary>
    /// 再起一个连接同一数据库的宿主，模拟多实例部署。由调用方释放。
    /// </summary>
    protected ApiFactory StartPeer(IReadOnlyDictionary<string, string>? settings = null)
    {
        var peer = new ApiFactory(_connectionString, settings ?? Settings);
        _ = peer.Server;
        return peer;
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

    /// <summary>
    /// 以当前身份调用平台动作，断言成功并返回输出。
    /// </summary>
    protected static async Task<JsonElement> InvokeAsync(HttpClient client, string key, object input)
    {
        var response = await PostAsync<InvokeActionResponse>(client, $"/api/actions/{key}/invoke", input);
        response.Status.Should().Be("succeeded", response.Error);
        return response.Output!.Value;
    }

    /// <summary>
    /// 错误响应（ProblemDetails）里的业务错误码。
    /// </summary>
    protected static async Task<string?> CodeAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString();

    protected static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>
    /// 添加一名指定角色的成员并返回其登录令牌。
    /// </summary>
    protected async Task<string> AddMemberAsync(HttpClient ownerClient, string email, Guid roleId)
    {
        await PostAsync<MemberDto>(ownerClient, "/api/members", new AddMemberRequest
        {
            Email = email,
            DisplayName = email.Split('@')[0],
            Password = "Passw0rd!x",
            RoleIds = [roleId],
        });
        var login = await PostAsync<AuthResponse>(Client(), "/api/auth/login", new LoginRequest { Email = email, Password = "Passw0rd!x" });
        return login.AccessToken;
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

    /// <summary>
    /// 等后台工作全部处理完：没有排队或已领取的唤醒，也没有排队或执行中的智能体运行、工作流运行。
    /// 唤醒和工作流运行都与引发它们的业务变化在同一事务里入队，此刻队列为空就说明之前的操作不会再引出新的工作，
    /// 用它代替"等一会儿再断言某件事没有发生"的固定延时。暂停的智能体的唤醒会一直排队，有这种情况的测试不能用它。
    /// </summary>
    protected async Task WaitForIdleAsync(TimeSpan? timeout = null)
    {
        await EventuallyAsync(
            () => WithDbAsync(async db => new
            {
                Wakeups = await db.WakeupRequests.IgnoreQueryFilters()
                    .CountAsync(w => w.Status == AiNative.Domain.Agents.WakeupStatus.Queued || w.Status == AiNative.Domain.Agents.WakeupStatus.Claimed, Ct),
                Runs = await db.HeartbeatRuns.IgnoreQueryFilters()
                    .CountAsync(r => r.Status == AiNative.Domain.Agents.RunStatus.Queued || r.Status == AiNative.Domain.Agents.RunStatus.Running, Ct),
                WorkflowRuns = await db.WorkflowRuns.IgnoreQueryFilters()
                    .CountAsync(r => r.Status == AiNative.Domain.Workflows.WorkflowRunStatus.Queued || r.Status == AiNative.Domain.Workflows.WorkflowRunStatus.Running, Ct),
            }),
            busy => busy.Wakeups == 0 && busy.Runs == 0 && busy.WorkflowRuns == 0,
            timeout);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            throw new HttpRequestException($"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");
        }
    }
}

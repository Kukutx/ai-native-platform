using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiNative.Application.Common;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Common;
using AiNative.Contracts.Knowledge;
using AiNative.Contracts.Platform;
using AiNative.Contracts.Work;
using AiNative.IntegrationTests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AiNative.IntegrationTests;

public sealed class ApiKeyAndMcpTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Api_key_acts_as_its_member_but_cannot_manage_keys()
    {
        var auth = await RegisterAsync();
        using var owner = Client(auth.AccessToken);
        var created = await PostAsync<CreatedApiKeyDto>(owner, "/api/api-keys", new CreateApiKeyRequest { Name = "脚本" });
        using var viaKey = Client(created.Token);

        var agents = await viaKey.GetAsync(new Uri("/api/agents", UriKind.Relative), Ct);
        var task = await PostAsync<WorkItemDto>(viaKey, "/api/work-items", new CreateWorkItemRequest { Title = "通过密钥创建" });
        var createKey = await viaKey.PostAsJsonAsync("/api/api-keys", new CreateApiKeyRequest { Name = "再来一把" }, PlatformJson.Options, Ct);
        var listKeys = await viaKey.GetAsync(new Uri("/api/api-keys", UriKind.Relative), Ct);

        created.Token.Should().StartWith(created.Key.Prefix);
        agents.StatusCode.Should().Be(HttpStatusCode.OK);
        task.CreatedBy.Name.Should().Be("owner", "密钥以成员本人的身份执行");
        createKey.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        listKeys.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var keys = await GetAsync<List<ApiKeyDto>>(owner, "/api/api-keys");
        keys.Should().ContainSingle().Which.LastUsedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Read_only_key_cannot_write()
    {
        var auth = await RegisterAsync();
        using var owner = Client(auth.AccessToken);
        var created = await PostAsync<CreatedApiKeyDto>(owner, "/api/api-keys", new CreateApiKeyRequest { Name = "只读", ReadOnly = true });
        using var viaKey = Client(created.Token);

        var read = await viaKey.GetAsync(new Uri("/api/work-items", UriKind.Relative), Ct);
        var write = await viaKey.PostAsJsonAsync("/api/work-items", new CreateWorkItemRequest { Title = "不允许" }, PlatformJson.Options, Ct);

        read.StatusCode.Should().Be(HttpStatusCode.OK);
        write.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Revoked_or_forged_keys_are_rejected()
    {
        var auth = await RegisterAsync();
        using var owner = Client(auth.AccessToken);
        var created = await PostAsync<CreatedApiKeyDto>(owner, "/api/api-keys", new CreateApiKeyRequest { Name = "临时" });
        using var viaKey = Client(created.Token);
        using var forged = Client(created.Token[..^4] + "AAAA");

        (await viaKey.GetAsync(new Uri("/api/agents", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await forged.GetAsync(new Uri("/api/agents", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var revoked = await PostAsync<ApiKeyDto>(owner, $"/api/api-keys/{created.Key.Id}/revoke", new { });
        var again = await owner.PostAsJsonAsync($"/api/api-keys/{created.Key.Id}/revoke", new { }, PlatformJson.Options, Ct);

        revoked.RevokedAt.Should().NotBeNull();
        again.IsSuccessStatusCode.Should().BeFalse();
        (await viaKey.GetAsync(new Uri("/api/agents", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "吊销立即生效");
    }

    [Fact]
    public async Task Mcp_client_lists_and_calls_platform_actions_with_key_permissions()
    {
        var auth = await RegisterAsync();
        using var owner = Client(auth.AccessToken);
        await PostAsync<KnowledgeDocumentDto>(owner, "/api/knowledge/documents", new SaveKnowledgeDocumentRequest
        {
            Title = "报销制度",
            Content = "## 报销时限\n出差结束后 15 个工作日内提交报销单。",
        });
        var full = await PostAsync<CreatedApiKeyDto>(owner, "/api/api-keys", new CreateApiKeyRequest { Name = "Claude Code" });
        var readOnly = await PostAsync<CreatedApiKeyDto>(owner, "/api/api-keys", new CreateApiKeyRequest { Name = "只读", ReadOnly = true });

        await using (var mcp = await ConnectMcpAsync(full.Token))
        {
            mcp.ServerInfo.Name.Should().Be("ai-native-platform");
            mcp.ServerInstructions.Should().Contain("工具");

            var tools = await mcp.ListToolsAsync(cancellationToken: Ct);
            tools.Select(t => t.Name).Should().Contain(["tasks_create", "knowledge_search", "memory_retain"]);
            tools.Single(t => t.Name == "knowledge_search").ProtocolTool.Annotations!.ReadOnlyHint.Should().BeTrue();
            tools.Single(t => t.Name == "tasks_create").ProtocolTool.Annotations!.ReadOnlyHint.Should().BeFalse();

            var create = await mcp.CallToolAsync("tasks_create", new Dictionary<string, object?> { ["title"] = "从 MCP 创建的任务" }, cancellationToken: Ct);
            var search = await mcp.CallToolAsync("knowledge_search", new Dictionary<string, object?> { ["query"] = "报销期限" }, cancellationToken: Ct);
            var unknown = await mcp.CallToolAsync("no_such_tool", new Dictionary<string, object?>(), cancellationToken: Ct);

            create.IsError.Should().BeFalse();
            Output(create).GetProperty("title").GetString().Should().Be("从 MCP 创建的任务");
            search.IsError.Should().BeFalse();
            Output(search)[0].GetProperty("content").GetString().Should().Contain("15 个工作日");
            unknown.IsError.Should().BeTrue();
        }

        await using (var mcp = await ConnectMcpAsync(readOnly.Token))
        {
            var tools = await mcp.ListToolsAsync(cancellationToken: Ct);
            tools.Should().NotBeEmpty();
            tools.Should().OnlyContain(t => t.ProtocolTool.Annotations!.ReadOnlyHint == true, "只读密钥只看得到只读工具");

            var denied = await mcp.CallToolAsync("tasks_create", new Dictionary<string, object?> { ["title"] = "不允许" }, cancellationToken: Ct);
            denied.IsError.Should().BeTrue();
        }

        var tasks = await GetAsync<PagedResult<WorkItemDto>>(owner, "/api/work-items");
        tasks.Items.Should().ContainSingle().Which.Title.Should().Be("从 MCP 创建的任务");
    }

    [Fact]
    public async Task Mcp_requires_authentication()
    {
        using var anonymous = Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await anonymous.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<McpClient> ConnectMcpAsync(string token)
    {
        var http = Client(token);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static JsonElement Output(CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        using var document = JsonDocument.Parse(text);
        return document.RootElement.GetProperty("output").Clone();
    }
}

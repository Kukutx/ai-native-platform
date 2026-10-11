using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using AiNative.Application.Common;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Auth;
using AiNative.Contracts.Common;
using AiNative.Contracts.Knowledge;
using AiNative.Contracts.Platform;
using AiNative.Contracts.Work;
using AiNative.Domain.Agents;
using AiNative.Domain.Knowledge;
using AiNative.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace AiNative.IntegrationTests;

public sealed class KnowledgeTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string Policy = """
        # 差旅报销

        ## 住宿标准
        一线城市每晚不超过 600 元，其他城市每晚不超过 400 元。

        ## 报销时限
        出差结束后 15 个工作日内提交报销单，逾期需要部门负责人特批。
        """;

    [Fact]
    public async Task Document_is_keyword_searchable_at_once_and_semantic_after_indexing()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);

        var document = await PostAsync<KnowledgeDocumentDto>(client, "/api/knowledge/documents", new SaveKnowledgeDocumentRequest
        {
            Title = "报销制度",
            Content = Policy,
            Tags = ["财务", "财务", " 制度 "],
        });

        document.ChunkCount.Should().BeGreaterThan(1);
        document.Tags.Should().Equal("财务", "制度");

        var status = await EventuallyAsync(() => GetAsync<KnowledgeStatusDto>(client, "/api/knowledge/status"), s => s.PendingEmbeddings == 0);
        status.IsDemoEmbedding.Should().BeTrue();
        status.Documents.Should().Be(1);

        var hits = await SearchAsync(client, "住宿每晚多少钱");
        hits.Should().NotBeEmpty();
        hits[0].SourceId.Should().Be(document.Id);
        hits[0].Title.Should().Be("报销制度");
        hits[0].Content.Should().Contain("600 元");
        hits[0].KeywordMatch.Should().BeTrue();
        hits[0].SemanticMatch.Should().BeTrue();

        var detail = await GetAsync<KnowledgeDocumentDetailDto>(client, $"/api/knowledge/documents/{document.Id}");
        detail.Chunks.Should().OnlyContain(c => c.Embedded);
        detail.Document.EmbeddedChunkCount.Should().Be(detail.Document.ChunkCount);
    }

    [Fact]
    public async Task Updating_a_document_rechunks_it_and_deleting_removes_it_from_search()
    {
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var document = await PostAsync<KnowledgeDocumentDto>(client, "/api/knowledge/documents", new SaveKnowledgeDocumentRequest { Title = "报销制度", Content = Policy });

        await PutAsync<KnowledgeDocumentDto>(client, $"/api/knowledge/documents/{document.Id}", new SaveKnowledgeDocumentRequest
        {
            Title = "报销制度",
            Content = "## 住宿标准\n所有城市每晚不超过 800 元。",
        });

        var hits = await SearchAsync(client, "住宿标准");
        hits.Should().ContainSingle().Which.Content.Should().Contain("800 元");

        var delete = await client.DeleteAsync(new Uri($"/api/knowledge/documents/{document.Id}", UriKind.Relative), Ct);
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SearchAsync(client, "住宿标准")).Should().BeEmpty();
        (await WithDbAsync(db => db.KnowledgeChunks.IgnoreQueryFilters().CountAsync(Ct))).Should().Be(0);
    }

    [Fact]
    public async Task Knowledge_is_isolated_between_spaces()
    {
        var alice = await RegisterAsync("alice@example.com", "A 公司");
        var bob = await RegisterAsync("bob@example.com", "B 公司");
        using var aliceClient = Client(alice.AccessToken);
        using var bobClient = Client(bob.AccessToken);
        var document = await PostAsync<KnowledgeDocumentDto>(aliceClient, "/api/knowledge/documents", new SaveKnowledgeDocumentRequest { Title = "报销制度", Content = Policy });
        await PostAsync<MemoryDto>(aliceClient, "/api/memories", new CreateMemoryRequest { Content = "A 公司的住宿标准是机密", Shared = true });
        await EventuallyAsync(() => GetAsync<KnowledgeStatusDto>(aliceClient, "/api/knowledge/status"), s => s.PendingEmbeddings == 0);

        var direct = await bobClient.GetAsync(new Uri($"/api/knowledge/documents/{document.Id}", UriKind.Relative), Ct);

        direct.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SearchAsync(bobClient, "住宿标准")).Should().BeEmpty();
        (await GetAsync<List<KnowledgeDocumentDto>>(bobClient, "/api/knowledge/documents")).Should().BeEmpty();
        (await GetAsync<PagedResult<MemoryDto>>(bobClient, "/api/memories")).Total.Should().Be(0);
    }

    [Fact]
    public async Task Viewer_can_search_but_cannot_write_knowledge_or_memories()
    {
        var owner = await RegisterAsync();
        using var ownerClient = Client(owner.AccessToken);
        await PostAsync<KnowledgeDocumentDto>(ownerClient, "/api/knowledge/documents", new SaveKnowledgeDocumentRequest { Title = "报销制度", Content = Policy });
        using var viewer = await MemberClientAsync(ownerClient, "viewer", "viewer@example.com");

        var write = await viewer.PostAsJsonAsync("/api/knowledge/documents", new SaveKnowledgeDocumentRequest { Title = "x", Content = "y" }, PlatformJson.Options, Ct);
        var remember = await viewer.PostAsJsonAsync("/api/memories", new CreateMemoryRequest { Content = "x" }, PlatformJson.Options, Ct);

        write.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        remember.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SearchAsync(viewer, "报销时限")).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Private_memories_are_visible_only_to_their_owner_and_shared_ones_to_everyone()
    {
        var owner = await RegisterAsync();
        using var ownerClient = Client(owner.AccessToken);
        using var alice = await MemberClientAsync(ownerClient, "member", "alice@example.com");
        using var bob = await MemberClientAsync(ownerClient, "member", "bob@example.com");

        var mine = await PostAsync<MemoryDto>(alice, "/api/memories", new CreateMemoryRequest { Content = "我习惯用表格写周报" });
        var again = await PostAsync<MemoryDto>(alice, "/api/memories", new CreateMemoryRequest { Content = "  我习惯用表格写周报 ", Tags = ["周报"] });
        var shared = await PostAsync<MemoryDto>(ownerClient, "/api/memories", new CreateMemoryRequest { Content = "周报每周五下午五点前提交", Shared = true });

        again.Id.Should().Be(mine.Id, "相同内容只保留一条");
        again.Tags.Should().Equal("周报");
        mine.Scope.Should().Be(MemoryScope.User);

        var bobSees = await GetAsync<PagedResult<MemoryDto>>(bob, "/api/memories");
        var bobSearch = await SearchAsync(bob, "周报");
        var aliceSearch = await SearchAsync(alice, "周报");
        var ownerSees = await GetAsync<PagedResult<MemoryDto>>(ownerClient, "/api/memories");

        bobSees.Items.Should().ContainSingle().Which.Id.Should().Be(shared.Id);
        bobSearch.Select(h => h.SourceId).Should().Equal(shared.Id);
        aliceSearch.Select(h => h.SourceId).Should().BeEquivalentTo([mine.Id, shared.Id]);
        ownerSees.Total.Should().Be(2, "有 memory.manage 的管理者可以看到全部记忆");

        var bobDeletesPrivate = await bob.DeleteAsync(new Uri($"/api/memories/{mine.Id}", UriKind.Relative), Ct);
        var bobDeletesShared = await bob.DeleteAsync(new Uri($"/api/memories/{shared.Id}", UriKind.Relative), Ct);
        var aliceDeletes = await alice.DeleteAsync(new Uri($"/api/memories/{mine.Id}", UriKind.Relative), Ct);

        bobDeletesPrivate.StatusCode.Should().Be(HttpStatusCode.NotFound);
        bobDeletesShared.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        aliceDeletes.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Agent_recalls_relevant_memories_and_knowledge_and_retains_new_ones()
    {
        var prompts = new ConcurrentQueue<string>();
        Factory.Script.Handler = (messages, _) =>
        {
            if (ScriptedModel.HasToolResult(messages))
            {
                return Task.FromResult(ScriptedModel.Reply("已记下。"));
            }

            prompts.Enqueue(string.Join("\n", messages.Where(m => m.Role == ChatRole.System || m.Role == ChatRole.User).Select(m => m.Text)));
            return Task.FromResult(ScriptedModel.CallTool("memory_retain", new Dictionary<string, object?>
            {
                ["content"] = "张三的会议室偏好：三楼大会议室",
            }));
        };
        var auth = await RegisterAsync();
        using var client = Client(auth.AccessToken);
        var agent = await StarterAgentAsync(client);
        await PostAsync<MemoryDto>(client, "/api/memories", new CreateMemoryRequest { Content = "客户张三只在周一上午开会", Shared = true });
        await PostAsync<MemoryDto>(client, "/api/memories", new CreateMemoryRequest { Content = "季度销售目标是一千万", Shared = true });
        await PostAsync<KnowledgeDocumentDto>(client, "/api/knowledge/documents", new SaveKnowledgeDocumentRequest
        {
            Title = "会议室预订规范",
            Content = "与客户开会需要提前一天预订会议室，并在日历里邀请客户。",
        });

        var task = await PostAsync<WorkItemDto>(client, "/api/work-items", new CreateWorkItemRequest
        {
            Title = "安排与客户张三开会",
            AssigneeAgentId = agent.Id,
        });

        var runs = await EventuallyAsync(RunsAsync, r => r.Count == 1 && r[0].Status == RunStatus.Succeeded);
        var prompt = prompts.Should().ContainSingle().Subject;
        prompt.Should().Contain("## 相关记忆").And.Contain("客户张三只在周一上午开会");
        prompt.Should().Contain("会议室预订规范");
        prompt.Should().Contain("memory_retain", "有记忆权限的智能体会被告知如何记下新知识");
        prompt.Should().NotContain("季度销售目标", "不相关的记忆不会被带入");

        var retained = await WithDbAsync(db => db.MemoryEntries.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.Content.StartsWith("张三的会议室"), Ct));
        retained.Scope.Should().Be(MemoryScope.Agent);
        retained.OwnerId.Should().Be(agent.Id);
        retained.RunId.Should().Be(runs[0].Id);
        retained.WorkItemId.Should().Be(task.Id);

        // 智能体的私有记忆不出现在成员自己的记忆里，但管理者可以按智能体查看。
        var ownSearch = await SearchAsync(client, "张三的会议室偏好");
        ownSearch.Should().NotContain(h => h.SourceId == retained.Id, "空间所有者检索时默认只看共享的和自己的");
        var asManager = await GetAsync<List<KnowledgeHitDto>>(client, $"/api/knowledge/search?q={Uri.EscapeDataString("张三的会议室偏好")}&agentId={agent.Id}");
        asManager.Should().Contain(h => h.SourceId == retained.Id);
    }

    [Fact]
    public async Task Concurrent_retains_of_the_same_memory_merge_and_new_tags_become_searchable()
    {
        var owner = await RegisterAsync();
        using var client = Client(owner.AccessToken);
        const string content = "团队周会固定在周一上午十点";

        var results = await Task.WhenAll(Enumerable.Range(1, 6).Select(i =>
            client.PostAsJsonAsync("/api/memories", new CreateMemoryRequest { Content = content, Shared = true, Tags = [$"标签{i}"] }, PlatformJson.Options, Ct)));
        var later = await PostAsync<MemoryDto>(client, "/api/memories", new CreateMemoryRequest { Content = content, Shared = true, Tags = ["季度复盘"] });
        var byNewTag = await SearchAsync(client, "季度复盘");

        results.Should().OnlyContain(r => r.IsSuccessStatusCode, "并发记下同一件事应合并，而不是撞唯一索引");
        (await GetAsync<PagedResult<MemoryDto>>(client, "/api/memories")).Total.Should().Be(1);
        later.Tags.Should().HaveCount(7);
        byNewTag.Select(h => h.SourceId).Should().Equal(later.Id);
    }

    private static Task<List<KnowledgeHitDto>> SearchAsync(HttpClient client, string query)
        => GetAsync<List<KnowledgeHitDto>>(client, $"/api/knowledge/search?q={Uri.EscapeDataString(query)}");

    private async Task<HttpClient> MemberClientAsync(HttpClient ownerClient, string roleKey, string email)
    {
        var role = (await GetAsync<List<RoleDto>>(ownerClient, "/api/roles")).Single(r => r.Key == roleKey);
        await PostAsync<MemberDto>(ownerClient, "/api/members", new AddMemberRequest
        {
            Email = email,
            DisplayName = email.Split('@')[0],
            Password = "Passw0rd!x",
            RoleIds = [role.Id],
        });
        var login = await PostAsync<AuthResponse>(Client(), "/api/auth/login", new LoginRequest { Email = email, Password = "Passw0rd!x" });
        return Client(login.AccessToken);
    }
}

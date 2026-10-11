using System.ComponentModel;
using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Actions;
using AiNative.Application.Modules;
using AiNative.Contracts.Knowledge;

namespace AiNative.Application.Knowledge.Actions;

public sealed class SearchKnowledgeInput
{
    [Description("要查找的内容，用自然语言描述问题或关键词")]
    public required string Query { get; init; }

    [Description("最多返回条数，默认 6")]
    public int Limit { get; init; } = 6;
}

internal sealed class SearchKnowledgeAction(IRequestDispatcher dispatcher) : PlatformAction<SearchKnowledgeInput, IReadOnlyList<KnowledgeHitDto>>
{
    public override string Key => "knowledge.search";

    public override string ModuleKey => KnowledgeModule.ModuleKey;

    public override string Title => "检索知识库";

    public override string Description => "在知识库文档中做语义 + 关键词混合检索，返回最相关的段落（含文档 ID 和标题）。回答业务问题、写方案前先查一查团队已有的资料；需要全文时再用 knowledge_get_document。";

    public override string RequiredFeature => Features.Knowledge.View;

    public override bool IsMutation => false;

    protected override Task<IReadOnlyList<KnowledgeHitDto>> ExecuteAsync(SearchKnowledgeInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new SearchKnowledgeQuery(input.Query, IncludeDocuments: true, IncludeMemories: false, Limit: Math.Clamp(input.Limit, 1, 20)), cancellationToken);
}

public sealed class GetKnowledgeDocumentInput
{
    [Description("文档 ID（来自检索结果的 sourceId）")]
    public required Guid DocumentId { get; init; }
}

public sealed record KnowledgeDocumentContent(Guid Id, string Title, IReadOnlyList<string> Tags, string Content, bool Truncated);

internal sealed class GetKnowledgeDocumentAction(IRequestDispatcher dispatcher) : PlatformAction<GetKnowledgeDocumentInput, KnowledgeDocumentContent>
{
    private const int MaxChars = 20_000;

    public override string Key => "knowledge.get_document";

    public override string ModuleKey => KnowledgeModule.ModuleKey;

    public override string Title => "查看知识库文档";

    public override string Description => $"读取知识库文档全文（超过 {MaxChars} 字会截断）。通常先用 knowledge_search 找到相关文档再读。";

    public override string RequiredFeature => Features.Knowledge.View;

    public override bool IsMutation => false;

    protected override async Task<KnowledgeDocumentContent> ExecuteAsync(GetKnowledgeDocumentInput input, ActionContext context, CancellationToken cancellationToken)
    {
        var detail = await dispatcher.SendAsync(new GetKnowledgeDocumentQuery(input.DocumentId), cancellationToken).ConfigureAwait(false);
        var truncated = detail.Content.Length > MaxChars;
        return new KnowledgeDocumentContent(
            detail.Document.Id,
            detail.Document.Title,
            detail.Document.Tags,
            truncated ? detail.Content[..MaxChars] : detail.Content,
            truncated);
    }
}

public sealed class CreateKnowledgeDocumentInput
{
    [Description("文档标题")]
    public required string Title { get; init; }

    [Description("正文，支持 Markdown")]
    public required string Content { get; init; }

    [Description("标签，便于分类检索")]
    public IReadOnlyList<string> Tags { get; init; } = [];
}

internal sealed class CreateKnowledgeDocumentAction(IRequestDispatcher dispatcher) : PlatformAction<CreateKnowledgeDocumentInput, KnowledgeDocumentDto>
{
    public override string Key => "knowledge.create_document";

    public override string ModuleKey => KnowledgeModule.ModuleKey;

    public override string Title => "写入知识库";

    public override string Description => "把一份完整的资料、方案、调研结论或操作手册保存进知识库，团队成员和其他智能体之后都能检索到。零散的事实或偏好用 memory_retain，不要建文档。";

    public override string RequiredFeature => Features.Knowledge.Manage;

    protected override Task<KnowledgeDocumentDto> ExecuteAsync(CreateKnowledgeDocumentInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new CreateKnowledgeDocumentCommand(new SaveKnowledgeDocumentRequest
        {
            Title = input.Title,
            Content = input.Content,
            Tags = input.Tags,
        }), cancellationToken);
}

public sealed class RetainMemoryInput
{
    [Description("要记住的内容：一条独立、具体、以后仍然成立的事实、偏好或约定，写成完整的一句话")]
    public required string Content { get; init; }

    [Description("为 true 时写入团队共享记忆（所有成员和智能体可见）；默认只记在自己名下")]
    public bool Shared { get; init; }

    [Description("标签，例如客户名、项目名")]
    public IReadOnlyList<string> Tags { get; init; } = [];
}

internal sealed class RetainMemoryAction(IRequestDispatcher dispatcher) : PlatformAction<RetainMemoryInput, MemoryDto>
{
    public override string Key => "memory.retain";

    public override string ModuleKey => KnowledgeModule.ModuleKey;

    public override string Title => "记住";

    public override string Description => "把以后还会用到的信息记入长期记忆：用户或客户的偏好、团队约定、踩过的坑、重要事实。下次被唤醒时相关记忆会自动出现在上下文里。不要记录一次性的过程信息或可以随时查到的数据。";

    public override string RequiredFeature => Features.Memory.Use;

    protected override Task<MemoryDto> ExecuteAsync(RetainMemoryInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new RetainMemoryCommand(input.Content, input.Shared, input.Tags, context.RunId, context.WorkItemId), cancellationToken);
}

public sealed class RecallMemoryInput
{
    [Description("想回忆什么，用自然语言描述")]
    public required string Query { get; init; }

    [Description("最多返回条数，默认 8")]
    public int Limit { get; init; } = 8;
}

internal sealed class RecallMemoryAction(IRequestDispatcher dispatcher) : PlatformAction<RecallMemoryInput, IReadOnlyList<KnowledgeHitDto>>
{
    public override string Key => "memory.recall";

    public override string ModuleKey => KnowledgeModule.ModuleKey;

    public override string Title => "回忆";

    public override string Description => "在自己的记忆和团队共享记忆中检索相关内容（语义 + 关键词）。自动带入的记忆不够用时再主动回忆。";

    public override string RequiredFeature => Features.Memory.Use;

    public override bool IsMutation => false;

    protected override Task<IReadOnlyList<KnowledgeHitDto>> ExecuteAsync(RecallMemoryInput input, ActionContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(new RecallMemoriesQuery(input.Query, Math.Clamp(input.Limit, 1, 20)), cancellationToken);
}

public sealed class ForgetMemoryInput
{
    [Description("记忆 ID（来自回忆结果的 sourceId）")]
    public required Guid MemoryId { get; init; }
}

public sealed record ForgetMemoryResult(Guid MemoryId, bool Forgotten);

internal sealed class ForgetMemoryAction(IRequestDispatcher dispatcher) : PlatformAction<ForgetMemoryInput, ForgetMemoryResult>
{
    public override string Key => "memory.forget";

    public override string ModuleKey => KnowledgeModule.ModuleKey;

    public override string Title => "忘记";

    public override string Description => "删除一条已经过时或错误的记忆。只能删除自己的记忆，或自己写入的共享记忆。";

    public override string RequiredFeature => Features.Memory.Use;

    protected override async Task<ForgetMemoryResult> ExecuteAsync(ForgetMemoryInput input, ActionContext context, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteMemoryCommand(input.MemoryId), cancellationToken).ConfigureAwait(false);
        return new ForgetMemoryResult(input.MemoryId, true);
    }
}

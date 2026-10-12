using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Application.Knowledge.Text;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Knowledge;
using AiNative.Domain.Knowledge;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Knowledge;

[RequireFeature(Features.Knowledge.View)]
public sealed record ListKnowledgeDocumentsQuery(string? Search = null, int Limit = 200) : IQuery<IReadOnlyList<KnowledgeDocumentDto>>;

[RequireFeature(Features.Knowledge.View)]
public sealed record GetKnowledgeDocumentQuery(Guid Id) : IQuery<KnowledgeDocumentDetailDto>;

[RequireFeature(Features.Knowledge.Manage)]
public sealed record CreateKnowledgeDocumentCommand(SaveKnowledgeDocumentRequest Request) : ICommand<KnowledgeDocumentDto>;

[RequireFeature(Features.Knowledge.Manage)]
public sealed record UpdateKnowledgeDocumentCommand(Guid Id, SaveKnowledgeDocumentRequest Request) : ICommand<KnowledgeDocumentDto>;

[RequireFeature(Features.Knowledge.Manage)]
public sealed record DeleteKnowledgeDocumentCommand(Guid Id) : ICommand<Unit>;

public static class KnowledgeLimits
{
    public const int MaxDocumentChars = 1_000_000;
    public const int MaxMemoryChars = 2000;
    public const int MaxTags = 20;
    public const int MaxTagLength = 50;
}

public sealed class CreateKnowledgeDocumentCommandValidator : AbstractValidator<CreateKnowledgeDocumentCommand>
{
    public CreateKnowledgeDocumentCommandValidator() => KnowledgeValidation.Document(this, x => x.Request);
}

public sealed class UpdateKnowledgeDocumentCommandValidator : AbstractValidator<UpdateKnowledgeDocumentCommand>
{
    public UpdateKnowledgeDocumentCommandValidator() => KnowledgeValidation.Document(this, x => x.Request);
}

internal static class KnowledgeValidation
{
    public static void Document<T>(AbstractValidator<T> validator, System.Linq.Expressions.Expression<Func<T, SaveKnowledgeDocumentRequest>> request)
    {
        validator.RuleFor(request).ChildRules(r =>
        {
            r.RuleFor(x => x.Title).NotEmpty().MaximumLength(300);
            r.RuleFor(x => x.Content).NotEmpty().MaximumLength(KnowledgeLimits.MaxDocumentChars);
            r.RuleFor(x => x.FileName).MaximumLength(260);
            Tags(r, x => x.Tags);
        });
    }

    public static void Tags<T>(AbstractValidator<T> validator, System.Linq.Expressions.Expression<Func<T, IEnumerable<string>>> tags)
    {
        validator.RuleFor(tags).Must(t => t is null || t.Count() <= KnowledgeLimits.MaxTags).WithMessage($"标签最多 {KnowledgeLimits.MaxTags} 个。");
        validator.RuleForEach(tags).NotEmpty().MaximumLength(KnowledgeLimits.MaxTagLength);
    }

    public static List<string> NormalizeTags(IEnumerable<string>? tags)
        => tags is null ? [] : [.. tags.Where(t => t is not null).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
}

/// <summary>
/// 把文档和记忆转换成可检索片段。文档标题参与关键词检索，正文切片保留标题路径。
/// </summary>
internal static class KnowledgeChunkFactory
{
    public static List<KnowledgeChunk> ForDocument(KnowledgeDocument document)
        => [.. TextChunker.Split(document.Content).Select((content, seq) => new KnowledgeChunk
        {
            TenantId = document.TenantId,
            SourceKind = KnowledgeSourceKind.Document,
            DocumentId = document.Id,
            Scope = MemoryScope.Tenant,
            Seq = seq,
            Content = content,
            SearchText = TextTokenizer.ToSearchText($"{document.Title}\n{string.Join(' ', document.Tags)}\n{content}"),
        })];

    public static KnowledgeChunk ForMemory(MemoryEntry memory) => new()
    {
        TenantId = memory.TenantId,
        SourceKind = KnowledgeSourceKind.Memory,
        MemoryId = memory.Id,
        Scope = memory.Scope,
        OwnerId = memory.OwnerId,
        Seq = 0,
        Content = memory.Content,
        SearchText = MemorySearchText(memory),
    };

    /// <summary>
    /// 记忆的标签只通过检索文本参与关键词匹配，标签变化时需要用它重建片段的检索文本。
    /// </summary>
    public static string MemorySearchText(MemoryEntry memory) => TextTokenizer.ToSearchText($"{memory.Content}\n{string.Join(' ', memory.Tags)}");
}

internal sealed class KnowledgeDocumentHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    IEmbeddingService embeddings,
    IKnowledgeIndexSignal indexSignal,
    IPostCommitActions postCommit,
    IChangeFeed changes,
    TimeProvider clock) :
    IRequestHandler<ListKnowledgeDocumentsQuery, IReadOnlyList<KnowledgeDocumentDto>>,
    IRequestHandler<GetKnowledgeDocumentQuery, KnowledgeDocumentDetailDto>,
    IRequestHandler<CreateKnowledgeDocumentCommand, KnowledgeDocumentDto>,
    IRequestHandler<UpdateKnowledgeDocumentCommand, KnowledgeDocumentDto>,
    IRequestHandler<DeleteKnowledgeDocumentCommand, Unit>
{
    public async Task<IReadOnlyList<KnowledgeDocumentDto>> HandleAsync(ListKnowledgeDocumentsQuery request, CancellationToken cancellationToken)
    {
        var query = db.KnowledgeDocuments.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim().ToLowerInvariant();
            query = query.Where(d => d.Title.ToLower().Contains(term) || d.Tags.Any(t => t.ToLower() == term));
        }

        var documents = await query.OrderByDescending(d => d.UpdatedAt).Take(Math.Clamp(request.Limit, 1, 500))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return await ToDtosAsync(documents, cancellationToken).ConfigureAwait(false);
    }

    public async Task<KnowledgeDocumentDetailDto> HandleAsync(GetKnowledgeDocumentQuery request, CancellationToken cancellationToken)
    {
        var document = await db.KnowledgeDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == request.Id, cancellationToken).ConfigureAwait(false)
                       ?? throw new NotFoundException("knowledge_document", request.Id);
        var model = embeddings.ModelId;
        var chunks = await db.KnowledgeChunks.AsNoTracking().Where(c => c.DocumentId == document.Id).OrderBy(c => c.Seq)
            .Select(c => new KnowledgeChunkDto(c.Id, c.Seq, c.Content, c.EmbeddingModel == model))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var dto = (await ToDtosAsync([document], cancellationToken).ConfigureAwait(false))[0];
        return new KnowledgeDocumentDetailDto(dto, document.Content, chunks);
    }

    public async Task<KnowledgeDocumentDto> HandleAsync(CreateKnowledgeDocumentCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var document = new KnowledgeDocument
        {
            Title = r.Title.Trim(),
            FileName = string.IsNullOrWhiteSpace(r.FileName) ? null : r.FileName.Trim(),
            Tags = KnowledgeValidation.NormalizeTags(r.Tags),
            CreatedBy = tenant.Actor,
        };
        var content = r.Content.Trim();
        document.SetContent(content, ContentHash.Of(content));
        db.KnowledgeDocuments.Add(document);
        Rechunk(document);
        changes.Publish(RealtimeEntities.KnowledgeDocument, document.Id, RealtimeChangeKind.Created);
        return (await ToDtosAsync([document], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<KnowledgeDocumentDto> HandleAsync(UpdateKnowledgeDocumentCommand request, CancellationToken cancellationToken)
    {
        var document = await db.KnowledgeDocuments.FirstOrDefaultAsync(d => d.Id == request.Id, cancellationToken).ConfigureAwait(false)
                       ?? throw new NotFoundException("knowledge_document", request.Id);
        var r = request.Request;
        var tags = KnowledgeValidation.NormalizeTags(r.Tags);
        var content = r.Content.Trim();
        var metadataChanged = document.Title != r.Title.Trim() || !document.Tags.SequenceEqual(tags);
        document.Title = r.Title.Trim();
        document.Tags = tags;
        if (r.FileName is not null)
        {
            document.FileName = string.IsNullOrWhiteSpace(r.FileName) ? null : r.FileName.Trim();
        }

        // 标题和标签也参与关键词检索，任一变化都重新切分。
        if (document.SetContent(content, ContentHash.Of(content)) || metadataChanged)
        {
            await db.KnowledgeChunks.Where(c => c.DocumentId == document.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            Rechunk(document);
        }

        changes.Publish(RealtimeEntities.KnowledgeDocument, document.Id, RealtimeChangeKind.Updated);
        return (await ToDtosAsync([document], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<Unit> HandleAsync(DeleteKnowledgeDocumentCommand request, CancellationToken cancellationToken)
    {
        var document = await db.KnowledgeDocuments.FirstOrDefaultAsync(d => d.Id == request.Id, cancellationToken).ConfigureAwait(false)
                       ?? throw new NotFoundException("knowledge_document", request.Id);
        db.KnowledgeDocuments.Remove(document);
        changes.Publish(RealtimeEntities.KnowledgeDocument, document.Id, RealtimeChangeKind.Deleted);
        return Unit.Value;
    }

    private void Rechunk(KnowledgeDocument document)
    {
        var chunks = KnowledgeChunkFactory.ForDocument(document);
        db.KnowledgeChunks.AddRange(chunks);
        document.MarkChunked(chunks.Count, clock.GetUtcNow());
        postCommit.Enqueue(_ =>
        {
            indexSignal.Notify();
            return Task.CompletedTask;
        });
    }

    private async Task<IReadOnlyList<KnowledgeDocumentDto>> ToDtosAsync(IReadOnlyList<KnowledgeDocument> documents, CancellationToken cancellationToken)
    {
        var ids = documents.Select(d => d.Id).ToList();
        var model = embeddings.ModelId;
        var embedded = await db.KnowledgeChunks.AsNoTracking()
            .Where(c => c.DocumentId != null && ids.Contains(c.DocumentId.Value) && c.EmbeddingModel == model)
            .GroupBy(c => c.DocumentId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, cancellationToken).ConfigureAwait(false);
        var actors = await ActorNames.ResolveAsync(db, documents.Select(d => d.CreatedBy), cancellationToken).ConfigureAwait(false);
        return [.. documents.Select(d => new KnowledgeDocumentDto(
            d.Id,
            d.Title,
            d.FileName,
            d.Tags,
            d.Content.Length,
            d.ChunkCount,
            embedded.GetValueOrDefault(d.Id),
            actors(d.CreatedBy),
            d.IndexedAt,
            d.CreatedAt,
            d.UpdatedAt))];
    }
}

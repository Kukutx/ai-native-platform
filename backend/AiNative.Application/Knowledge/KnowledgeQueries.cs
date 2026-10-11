using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Knowledge;
using AiNative.Domain.Knowledge;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Knowledge;

/// <summary>
/// 检索知识库（有 memory.use 时同时检索可见的记忆）。
/// </summary>
[RequireFeature(Features.Knowledge.View)]
public sealed record SearchKnowledgeQuery(
    string Query,
    bool IncludeDocuments = true,
    bool IncludeMemories = true,
    int Limit = 10,
    Guid? AgentId = null) : IQuery<IReadOnlyList<KnowledgeHitDto>>;

/// <summary>
/// 回忆：只检索记忆（共享的和自己的），并记录被回忆的次数。
/// </summary>
[RequireFeature(Features.Memory.Use)]
public sealed record RecallMemoriesQuery(string Query, int Limit = 8) : IQuery<IReadOnlyList<KnowledgeHitDto>>;

[RequireFeature(Features.Knowledge.View)]
public sealed record GetKnowledgeStatusQuery : IQuery<KnowledgeStatusDto>;

public sealed class SearchKnowledgeQueryValidator : AbstractValidator<SearchKnowledgeQuery>
{
    public SearchKnowledgeQueryValidator()
    {
        RuleFor(x => x.Query).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.Limit).InclusiveBetween(1, 50);
    }
}

public sealed class RecallMemoriesQueryValidator : AbstractValidator<RecallMemoriesQuery>
{
    public RecallMemoriesQueryValidator()
    {
        RuleFor(x => x.Query).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.Limit).InclusiveBetween(1, 30);
    }
}

internal sealed class KnowledgeQueryHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    IKnowledgeSearch search,
    IEmbeddingService embeddings,
    TimeProvider clock) :
    IRequestHandler<SearchKnowledgeQuery, IReadOnlyList<KnowledgeHitDto>>,
    IRequestHandler<RecallMemoriesQuery, IReadOnlyList<KnowledgeHitDto>>,
    IRequestHandler<GetKnowledgeStatusQuery, KnowledgeStatusDto>
{
    public async Task<IReadOnlyList<KnowledgeHitDto>> HandleAsync(SearchKnowledgeQuery request, CancellationToken cancellationToken)
    {
        var hits = await search.SearchAsync(new KnowledgeSearchRequest
        {
            Query = request.Query,
            IncludeDocuments = request.IncludeDocuments,
            IncludeMemories = request.IncludeMemories && tenant.Can(Features.Memory.Use),
            Limit = request.Limit,
            AlsoAgentMemoriesOf = tenant.Can(Features.Memory.Manage) ? request.AgentId : null,
        }, cancellationToken).ConfigureAwait(false);
        return await KnowledgeHitMapping.ToDtosAsync(db, tenant, hits, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<KnowledgeHitDto>> HandleAsync(RecallMemoriesQuery request, CancellationToken cancellationToken)
    {
        var hits = await search.SearchAsync(new KnowledgeSearchRequest
        {
            Query = request.Query,
            IncludeDocuments = false,
            IncludeMemories = true,
            Limit = request.Limit,
        }, cancellationToken).ConfigureAwait(false);

        var ids = hits.Select(h => h.SourceId).ToList();
        if (ids.Count > 0)
        {
            var now = clock.GetUtcNow();
            await db.MemoryEntries.Where(m => ids.Contains(m.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.RecallCount, m => m.RecallCount + 1).SetProperty(m => m.LastRecalledAt, now), cancellationToken)
                .ConfigureAwait(false);
        }

        return await KnowledgeHitMapping.ToDtosAsync(db, tenant, hits, cancellationToken).ConfigureAwait(false);
    }

    public async Task<KnowledgeStatusDto> HandleAsync(GetKnowledgeStatusQuery request, CancellationToken cancellationToken)
    {
        var model = embeddings.ModelId;
        var documents = await db.KnowledgeDocuments.CountAsync(cancellationToken).ConfigureAwait(false);
        var memories = await MemoryMapping.Visible(db.MemoryEntries, tenant).CountAsync(cancellationToken).ConfigureAwait(false);
        var pending = await db.KnowledgeChunks.CountAsync(c => c.EmbeddingModel == null || c.EmbeddingModel != model, cancellationToken).ConfigureAwait(false);
        return new KnowledgeStatusDto(model, embeddings.IsDemo, documents, memories, pending);
    }
}

internal static class KnowledgeHitMapping
{
    public static async Task<IReadOnlyList<KnowledgeHitDto>> ToDtosAsync(
        IAppDbContext db,
        ITenantContext tenant,
        IReadOnlyList<KnowledgeHit> hits,
        CancellationToken cancellationToken)
    {
        var documentIds = hits.Where(h => h.SourceKind == KnowledgeSourceKind.Document).Select(h => h.SourceId).Distinct().ToList();
        var memoryIds = hits.Where(h => h.SourceKind == KnowledgeSourceKind.Memory).Select(h => h.SourceId).Distinct().ToList();
        var documents = await db.KnowledgeDocuments.AsNoTracking().Where(d => documentIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Title, d.CreatedAt })
            .ToDictionaryAsync(d => d.Id, cancellationToken).ConfigureAwait(false);
        var memories = await db.MemoryEntries.AsNoTracking().Where(m => memoryIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Scope, m.OwnerId, m.CreatedAt })
            .ToDictionaryAsync(m => m.Id, cancellationToken).ConfigureAwait(false);

        var result = new List<KnowledgeHitDto>(hits.Count);
        foreach (var hit in hits)
        {
            if (hit.SourceKind == KnowledgeSourceKind.Document && documents.TryGetValue(hit.SourceId, out var document))
            {
                result.Add(new KnowledgeHitDto(hit.SourceKind, hit.SourceId, document.Title, hit.Seq, hit.Content, Math.Round(hit.Score, 6),
                    hit.SemanticMatch, hit.KeywordMatch, null, document.CreatedAt));
            }
            else if (hit.SourceKind == KnowledgeSourceKind.Memory && memories.TryGetValue(hit.SourceId, out var memory))
            {
                var title = memory.Scope switch
                {
                    MemoryScope.Tenant => "共享记忆",
                    _ when memory.OwnerId == tenant.Actor.Id => "我的记忆",
                    MemoryScope.Agent => "智能体记忆",
                    _ => "成员记忆",
                };
                result.Add(new KnowledgeHitDto(hit.SourceKind, hit.SourceId, title, hit.Seq, hit.Content, Math.Round(hit.Score, 6),
                    hit.SemanticMatch, hit.KeywordMatch, memory.Scope, memory.CreatedAt));
            }
        }

        return result;
    }
}

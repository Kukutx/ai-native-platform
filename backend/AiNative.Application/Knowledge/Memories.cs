using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Common;
using AiNative.Contracts.Knowledge;
using AiNative.Domain.Common;
using AiNative.Domain.Knowledge;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Knowledge;

/// <summary>
/// 记下一条长期记忆。同一归属下内容相同（忽略大小写和空白）的记忆只保留一条。
/// </summary>
[RequireFeature(Features.Memory.Use)]
public sealed record RetainMemoryCommand(
    string Content,
    bool Shared,
    IReadOnlyList<string> Tags,
    Guid? RunId = null,
    Guid? WorkItemId = null) : ICommand<MemoryDto>;

/// <summary>
/// 列出记忆：默认是共享记忆和自己的记忆；有 memory.manage 时可以看全部，并按归属筛选。
/// </summary>
[RequireFeature(Features.Memory.Use)]
public sealed record ListMemoriesQuery(MemoryScope? Scope = null, Guid? OwnerId = null, string? Search = null, int Page = 1, int PageSize = 50)
    : IQuery<PagedResult<MemoryDto>>;

[RequireFeature(Features.Memory.Use)]
public sealed record DeleteMemoryCommand(Guid Id) : ICommand<Unit>;

public sealed class RetainMemoryCommandValidator : AbstractValidator<RetainMemoryCommand>
{
    public RetainMemoryCommandValidator()
    {
        RuleFor(x => x.Content).NotEmpty().MaximumLength(KnowledgeLimits.MaxMemoryChars);
        KnowledgeValidation.Tags(this, x => x.Tags);
    }
}

internal sealed class MemoryHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    IKnowledgeIndexSignal indexSignal,
    IPostCommitActions postCommit,
    ITransactionLock locks,
    IChangeFeed changes) :
    IRequestHandler<RetainMemoryCommand, MemoryDto>,
    IRequestHandler<ListMemoriesQuery, PagedResult<MemoryDto>>,
    IRequestHandler<DeleteMemoryCommand, Unit>
{
    public async Task<MemoryDto> HandleAsync(RetainMemoryCommand request, CancellationToken cancellationToken)
    {
        var content = request.Content.Trim();
        var hash = ContentHash.OfNormalized(content);
        var (scope, ownerId) = MemoryEntry.OwnedBy(tenant.Actor, request.Shared);
        var tags = KnowledgeValidation.NormalizeTags(request.Tags);

        // 同一归属下记同一件事在事务内串行化：并发写入时后到的一方会读到先到的记忆并合并，而不是撞唯一索引。
        await locks.AcquireAsync($"memory:{tenant.TenantId}:{scope}:{ownerId}:{hash}", cancellationToken).ConfigureAwait(false);

        var existing = await db.MemoryEntries
            .FirstOrDefaultAsync(m => m.Scope == scope && m.OwnerId == ownerId && m.ContentHash == hash, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            // 重复记下说明这件事仍然重要：合并标签并刷新时间，回忆时的时间排序会把它往前放。
            var merged = KnowledgeValidation.NormalizeTags(existing.Tags.Concat(tags));
            if (merged.Count != existing.Tags.Count)
            {
                existing.Tags = merged;
                var searchText = KnowledgeChunkFactory.MemorySearchText(existing);
                await db.KnowledgeChunks.Where(c => c.MemoryId == existing.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.SearchText, searchText), cancellationToken)
                    .ConfigureAwait(false);
            }

            db.Entry(existing).Property(m => m.UpdatedAt).IsModified = true;
            changes.Publish(RealtimeEntities.Memory, existing.Id, RealtimeChangeKind.Updated);
            return (await MemoryMapping.ToDtosAsync(db, [existing], cancellationToken).ConfigureAwait(false))[0];
        }

        var memory = new MemoryEntry
        {
            Scope = scope,
            OwnerId = ownerId,
            Content = content,
            ContentHash = hash,
            Tags = tags,
            CreatedBy = tenant.Actor,
            RunId = request.RunId,
            WorkItemId = request.WorkItemId,
        };
        db.MemoryEntries.Add(memory);
        db.KnowledgeChunks.Add(KnowledgeChunkFactory.ForMemory(memory));
        postCommit.Enqueue(_ =>
        {
            indexSignal.Notify();
            return Task.CompletedTask;
        });
        changes.Publish(RealtimeEntities.Memory, memory.Id, RealtimeChangeKind.Created);
        return (await MemoryMapping.ToDtosAsync(db, [memory], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<PagedResult<MemoryDto>> HandleAsync(ListMemoriesQuery request, CancellationToken cancellationToken)
    {
        var query = MemoryMapping.Visible(db.MemoryEntries.AsNoTracking(), tenant);
        if (request.Scope is { } scope)
        {
            query = query.Where(m => m.Scope == scope);
        }

        if (request.OwnerId is { } ownerId)
        {
            query = query.Where(m => m.OwnerId == ownerId);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim().ToLowerInvariant();
            query = query.Where(m => m.Content.ToLower().Contains(term));
        }

        var page = Math.Max(request.Page, 1);
        var size = Math.Clamp(request.PageSize, 1, 200);
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query.OrderByDescending(m => m.UpdatedAt).Skip((page - 1) * size).Take(size)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<MemoryDto>(await MemoryMapping.ToDtosAsync(db, items, cancellationToken).ConfigureAwait(false), total, page, size);
    }

    public async Task<Unit> HandleAsync(DeleteMemoryCommand request, CancellationToken cancellationToken)
    {
        var memory = await db.MemoryEntries.FirstOrDefaultAsync(m => m.Id == request.Id, cancellationToken).ConfigureAwait(false);
        if (memory is null || !(tenant.Can(Features.Memory.Manage) || memory.IsVisibleTo(tenant.Actor)))
        {
            throw new NotFoundException("memory", request.Id);
        }

        // 共享记忆只有记下它的人（或管理者）可以删除。
        if (!tenant.Can(Features.Memory.Manage) && memory.Scope == MemoryScope.Tenant && memory.CreatedBy != tenant.Actor)
        {
            throw new ForbiddenException("只能删除自己记下的共享记忆。");
        }

        db.MemoryEntries.Remove(memory);
        changes.Publish(RealtimeEntities.Memory, memory.Id, RealtimeChangeKind.Deleted);
        return Unit.Value;
    }
}

internal static class MemoryMapping
{
    /// <summary>
    /// 当前身份可见的记忆。与 <see cref="MemoryEntry.IsVisibleTo"/> 同一规则，翻译成 SQL。
    /// </summary>
    public static IQueryable<MemoryEntry> Visible(IQueryable<MemoryEntry> query, ITenantContext tenant)
    {
        if (tenant.Can(Features.Memory.Manage))
        {
            return query;
        }

        var actor = tenant.Actor;
        var agentId = actor.IsAgent ? actor.Id : null;
        var userId = actor.IsUser ? actor.Id : null;
        return query.Where(m => m.Scope == MemoryScope.Tenant
                                || (m.Scope == MemoryScope.Agent && agentId != null && m.OwnerId == agentId)
                                || (m.Scope == MemoryScope.User && userId != null && m.OwnerId == userId));
    }

    public static async Task<IReadOnlyList<MemoryDto>> ToDtosAsync(IAppDbContext db, IReadOnlyList<MemoryEntry> memories, CancellationToken cancellationToken)
    {
        var owners = memories.Select(m => m.Scope switch
        {
            MemoryScope.Agent when m.OwnerId is { } id => Actor.ForAgent(id),
            MemoryScope.User when m.OwnerId is { } id => Actor.ForUser(id),
            _ => Actor.System,
        }).ToList();
        var actors = await ActorNames.ResolveAsync(db, memories.Select(m => m.CreatedBy).Concat(owners), cancellationToken).ConfigureAwait(false);
        return [.. memories.Select((m, i) => new MemoryDto(
            m.Id,
            m.Scope,
            m.Scope == MemoryScope.Tenant ? null : actors(owners[i]),
            m.Content,
            m.Tags,
            actors(m.CreatedBy),
            m.RunId,
            m.WorkItemId,
            m.RecallCount,
            m.LastRecalledAt,
            m.CreatedAt))];
    }
}

using AiNative.Application.Knowledge;
using AiNative.Application.Realtime;
using AiNative.Contracts.Realtime;
using AiNative.Infrastructure.Common;
using AiNative.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace AiNative.Infrastructure.Knowledge;

/// <summary>
/// 通知向量化后台有新片段。
/// </summary>
internal sealed class KnowledgeIndexSignal : CoalescingSignal, IKnowledgeIndexSignal;

/// <summary>
/// 后台向量化：找出没有向量或向量模型与当前配置不一致的片段，批量生成向量写回。
/// 片段一经生成就不再修改（文档更新时整体重建），所以不需要加锁：写回时片段已被删除则什么都不发生，
/// 多实例同时处理同一批只会多调用一次模型。更换向量模型后，旧向量会被自动逐批替换。
/// 整批失败时逐条重试：个别内容被模型拒绝（超长、含违规内容）只标记这几条不再处理，不会卡住后面的文档；
/// 逐条也全部失败才视为向量服务故障，退避后重试。写回后推送文档和记忆的变化，界面上的向量化进度实时刷新。
/// </summary>
internal sealed class KnowledgeIndexer(
    IServiceScopeFactory scopes,
    KnowledgeIndexSignal signal,
    IEmbeddingService embeddings,
    TimeProvider clock,
    ILogger<KnowledgeIndexer> logger) : BackgroundService
{
    private const int BatchSize = 64;

    // 逐条重试时，开头连续这么多条都失败、一条都没成功，就认为是服务故障而不是个别内容的问题。
    private const int FailFastAfter = 3;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    // 启动后先全表找一次"模型不一致"的旧向量（更换模型后需要），之后只看新片段（走部分索引）。
    private bool _staleScanDone;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.Zero;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await EmbedPendingAsync(stoppingToken).ConfigureAwait(false);
                backoff = TimeSpan.Zero;
                if (processed == BatchSize)
                {
                    continue;
                }

                await signal.WaitAsync(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 向量服务出错（密钥失效、限流、网络）时指数退避，关键词检索不受影响。
                backoff = backoff == TimeSpan.Zero ? TimeSpan.FromSeconds(5) : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                logger.LogWarning(ex, "Embedding knowledge chunks failed; retrying in {Backoff}", backoff);
                try
                {
                    await Task.Delay(backoff, clock, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// 处理一批待向量化片段，返回处理条数。
    /// </summary>
    internal async Task<int> EmbedPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var model = embeddings.ModelId;

        var condition = _staleScanDone ? "c.embedding_model IS NULL" : "c.embedding_model IS DISTINCT FROM @model";
        var pending = await RawSql.RunAsync(db.Database, async command =>
        {
            command.CommandText = $"""
                SELECT c.id, c.tenant_id, c.document_id, c.memory_id, COALESCE(d.title || E'\n', '') || c.content
                FROM knowledge_chunks c
                LEFT JOIN knowledge_documents d ON d.id = c.document_id
                WHERE {condition}
                ORDER BY c.created_at
                LIMIT @batch
                """;
            command.Parameters.Add(new NpgsqlParameter("model", model));
            command.Parameters.Add(new NpgsqlParameter("batch", BatchSize));
            var rows = new List<PendingChunk>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new PendingChunk(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    reader.GetString(4)));
            }

            return rows;
        }, cancellationToken).ConfigureAwait(false);

        if (pending.Count == 0)
        {
            _staleScanDone = true;
            return 0;
        }

        var vectors = await EmbedAsync([.. pending.Select(p => p.Text)], cancellationToken).ConfigureAwait(false);

        // 没有任何可用字词的片段（例如只有标点）得到零向量、被模型拒绝的片段没有向量，都存 NULL 并标记模型，避免反复处理。
        var literals = vectors.Select(v => v is not null && v.Any(x => x != 0) ? EmbeddingVector.ToSql(v) : null).ToArray();
        await RawSql.RunAsync(db.Database, async command =>
        {
            command.CommandText = """
                UPDATE knowledge_chunks AS c
                SET embedding = v.embedding::vector, embedding_model = @model
                FROM unnest(@ids, @vectors) AS v(id, embedding)
                WHERE c.id = v.id
                """;
            command.Parameters.Add(new NpgsqlParameter("model", model));
            command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = pending.Select(p => p.Id).ToArray() });
            command.Parameters.Add(new NpgsqlParameter("vectors", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = literals });
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        // 最后一批旧向量写回成功后才切换到只看新片段；中途出错时下一轮仍会重新扫描旧向量。
        if (pending.Count < BatchSize)
        {
            _staleScanDone = true;
        }

        logger.LogDebug("Embedded {Count} knowledge chunks with {Model}", pending.Count, model);
        await NotifyAsync(scope.ServiceProvider, pending, cancellationToken).ConfigureAwait(false);
        return pending.Count;
    }

    /// <summary>
    /// 整批生成向量；整批失败时逐条重试，被拒绝的条目返回 null。逐条也都失败时抛出最后一个异常。
    /// </summary>
    private async Task<IReadOnlyList<float[]?>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        try
        {
            return await embeddings.EmbedAsync(texts, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (texts.Count > 1 && ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Embedding a batch of {Count} chunks failed; retrying one by one", texts.Count);
        }

        var results = new float[]?[texts.Count];
        var succeeded = 0;
        Exception? last = null;
        for (var i = 0; i < texts.Count; i++)
        {
            try
            {
                results[i] = (await embeddings.EmbedAsync([texts[i]], cancellationToken).ConfigureAwait(false))[0];
                succeeded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                if (succeeded == 0 && i + 1 >= FailFastAfter)
                {
                    throw;
                }
            }
        }

        if (succeeded == 0)
        {
            throw last!;
        }

        if (succeeded < texts.Count)
        {
            logger.LogWarning(last, "{Failed} of {Count} chunks were rejected by the embedding model and will be searched by keywords only", texts.Count - succeeded, texts.Count);
        }

        return results;
    }

    /// <summary>
    /// 推送是尽力而为的：向量已经写回，推送失败只记日志。
    /// </summary>
    private async Task NotifyAsync(IServiceProvider services, IReadOnlyList<PendingChunk> chunks, CancellationToken cancellationToken)
    {
        if (services.GetService<IRealtimeNotifier>() is not { } notifier)
        {
            return;
        }

        var changes = chunks.Where(c => c.DocumentId is not null).Select(c => (c.TenantId, Entity: RealtimeEntities.KnowledgeDocument, Id: c.DocumentId!.Value))
            .Concat(chunks.Where(c => c.MemoryId is not null).Select(c => (c.TenantId, Entity: RealtimeEntities.Memory, Id: c.MemoryId!.Value)))
            .Distinct();
        try
        {
            foreach (var (tenantId, entity, id) in changes)
            {
                await notifier.ChangedAsync(tenantId, new RealtimeChangeDto(entity, id, RealtimeChangeKind.Updated), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Pushing embedding progress failed");
        }
    }

    private sealed record PendingChunk(Guid Id, Guid TenantId, Guid? DocumentId, Guid? MemoryId, string Text);
}

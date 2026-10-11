using AiNative.Application.Knowledge;
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
/// </summary>
internal sealed class KnowledgeIndexer(
    IServiceScopeFactory scopes,
    KnowledgeIndexSignal signal,
    IEmbeddingService embeddings,
    TimeProvider clock,
    ILogger<KnowledgeIndexer> logger) : BackgroundService
{
    private const int BatchSize = 64;
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
                SELECT c.id, COALESCE(d.title || E'\n', '') || c.content
                FROM knowledge_chunks c
                LEFT JOIN knowledge_documents d ON d.id = c.document_id
                WHERE {condition}
                ORDER BY c.created_at
                LIMIT @batch
                """;
            command.Parameters.Add(new NpgsqlParameter("model", model));
            command.Parameters.Add(new NpgsqlParameter("batch", BatchSize));
            var rows = new List<(Guid Id, string Text)>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((reader.GetGuid(0), reader.GetString(1)));
            }

            return rows;
        }, cancellationToken).ConfigureAwait(false);

        if (pending.Count < BatchSize)
        {
            _staleScanDone = true;
        }

        if (pending.Count == 0)
        {
            return 0;
        }

        var vectors = await embeddings.EmbedAsync([.. pending.Select(p => p.Text)], cancellationToken).ConfigureAwait(false);

        // 没有任何可用字词的片段（例如只有标点）得到零向量，存 NULL 并标记模型，避免反复处理。
        var literals = vectors.Select(v => v.Any(x => x != 0) ? EmbeddingVector.ToSql(v) : null).ToArray();
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

        logger.LogDebug("Embedded {Count} knowledge chunks with {Model}", pending.Count, model);
        return pending.Count;
    }
}

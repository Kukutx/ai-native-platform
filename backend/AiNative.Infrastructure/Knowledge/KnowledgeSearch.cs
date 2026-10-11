using System.Data.Common;
using AiNative.Application.Knowledge;
using AiNative.Application.Knowledge.Text;
using AiNative.Application.Tenancy;
using AiNative.Domain.Knowledge;
using AiNative.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace AiNative.Infrastructure.Knowledge;

/// <summary>
/// 混合检索的 PostgreSQL 实现：pgvector HNSW 近邻 + 分词 tsvector 关键词，各取候选后按倒数排名融合（RRF，k = 60）。
/// 原生 SQL 绕过了 EF 的租户过滤器，所以租户和可见性条件都在这里显式写出。
/// </summary>
internal sealed class KnowledgeSearch(
    AppDbContext db,
    ITenantContext tenant,
    IEmbeddingService embeddings,
    ILogger<KnowledgeSearch> logger) : IKnowledgeSearch
{
    private const int RrfK = 60;

    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(KnowledgeSearchRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.IncludeDocuments && !request.IncludeMemories)
        {
            return [];
        }

        var limit = Math.Clamp(request.Limit, 1, 50);
        var candidates = Math.Max(limit * 4, 20);
        var tsQuery = TextTokenizer.ToTsQuery(request.Query);
        var vector = await TryEmbedAsync(request.Query, cancellationToken).ConfigureAwait(false);
        if (vector is null && tsQuery is null)
        {
            return [];
        }

        var actor = tenant.Actor;
        var agentOwners = new List<Guid>();
        if (actor.IsAgent && actor.Id is { } agentId)
        {
            agentOwners.Add(agentId);
        }

        if (request.AlsoAgentMemoriesOf is { } extraAgent)
        {
            agentOwners.Add(extraAgent);
        }

        var visibility = new List<string>();
        if (request.IncludeDocuments)
        {
            visibility.Add("source_kind = 'Document'");
        }

        if (request.IncludeMemories)
        {
            visibility.Add("""
                (source_kind = 'Memory' AND (scope = 'Tenant'
                    OR (scope = 'Agent' AND owner_id = ANY(@agent_owners))
                    OR (scope = 'User' AND owner_id = @user_owner)))
                """);
        }

        var filter = $"tenant_id = @tenant AND ({string.Join(" OR ", visibility)})";
        var vecCte = vector is null
            ? "SELECT NULL::uuid AS id, NULL::bigint AS rank WHERE false"
            : $"""
               SELECT id, row_number() OVER (ORDER BY distance) AS rank
               FROM (
                   SELECT id, embedding <=> @embedding::vector AS distance
                   FROM knowledge_chunks
                   WHERE {filter} AND embedding_model = @model AND embedding IS NOT NULL
                   ORDER BY embedding <=> @embedding::vector
                   LIMIT @candidates
               ) nearest
               WHERE distance <= @max_distance
               """;
        var lexCte = tsQuery is null
            ? "SELECT NULL::uuid AS id, NULL::bigint AS rank WHERE false"
            : $"""
               SELECT id, row_number() OVER (ORDER BY score DESC) AS rank
               FROM (
                   SELECT id, ts_rank(search_vector, q, 1) AS score
                   FROM knowledge_chunks, CAST(@tsquery AS tsquery) AS q
                   WHERE {filter} AND search_vector @@ q
                   ORDER BY score DESC
                   LIMIT @candidates
               ) matched
               """;

        // HNSW 在带过滤条件时可能凑不满候选，迭代扫描（pgvector 0.8+）会继续往下找；SET LOCAL 只作用于本次隐式事务。
        var sql = $"""
            SELECT set_config('hnsw.iterative_scan', 'relaxed_order', true), set_config('hnsw.ef_search', '100', true);
            WITH vec AS ({vecCte}), lex AS ({lexCte})
            SELECT c.id, c.source_kind, COALESCE(c.document_id, c.memory_id) AS source_id, c.seq, c.content,
                   COALESCE(1.0::float8 / ({RrfK} + vec.rank), 0) + COALESCE(1.0::float8 / ({RrfK} + lex.rank), 0) AS score,
                   vec.id IS NOT NULL AS semantic, lex.id IS NOT NULL AS keyword
            FROM (SELECT id FROM vec UNION SELECT id FROM lex) ids
            JOIN knowledge_chunks c ON c.id = ids.id
            LEFT JOIN vec ON vec.id = c.id
            LEFT JOIN lex ON lex.id = c.id
            ORDER BY score DESC, c.id
            LIMIT @limit;
            """;

        return await RawSql.RunAsync(db.Database, async command =>
        {
            command.CommandText = sql;
            command.Parameters.Add(new NpgsqlParameter("tenant", tenant.TenantId));
            command.Parameters.Add(new NpgsqlParameter("agent_owners", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = agentOwners.ToArray() });
            command.Parameters.Add(new NpgsqlParameter("user_owner", actor.IsUser && actor.Id is { } userId ? userId : Guid.Empty));
            command.Parameters.Add(new NpgsqlParameter("candidates", candidates));
            command.Parameters.Add(new NpgsqlParameter("limit", limit));
            if (vector is not null)
            {
                command.Parameters.Add(new NpgsqlParameter("embedding", EmbeddingVector.ToSql(vector)));
                command.Parameters.Add(new NpgsqlParameter("model", embeddings.ModelId));
                command.Parameters.Add(new NpgsqlParameter("max_distance", embeddings.MaxDistance));
            }

            if (tsQuery is not null)
            {
                command.Parameters.Add(new NpgsqlParameter("tsquery", tsQuery));
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            return await ReadHitsAsync(reader, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<float[]?> TryEmbedAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            var vector = (await embeddings.EmbedAsync([query], cancellationToken).ConfigureAwait(false))[0];
            return vector.Any(v => v != 0) ? vector : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 向量服务不可用时降级为纯关键词检索，不影响调用方。
            logger.LogWarning(ex, "Embedding the search query failed; falling back to keyword search.");
            return null;
        }
    }

    private static async Task<IReadOnlyList<KnowledgeHit>> ReadHitsAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        var hits = new List<KnowledgeHit>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            hits.Add(new KnowledgeHit(
                reader.GetGuid(0),
                Enum.Parse<KnowledgeSourceKind>(reader.GetString(1)),
                reader.GetGuid(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetDouble(5),
                reader.GetBoolean(6),
                reader.GetBoolean(7)));
        }

        return hits;
    }
}

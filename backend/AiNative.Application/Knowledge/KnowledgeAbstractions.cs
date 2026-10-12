using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AiNative.Domain.Knowledge;

namespace AiNative.Application.Knowledge;

/// <summary>
/// 向量化服务。实现负责把各家模型的输出统一成 <see cref="EmbeddingVector.Dimensions"/> 维的单位向量。
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// 当前向量模型标识（提供方/模型）。只有同一模型生成的向量之间才可比较。
    /// </summary>
    string ModelId { get; }

    /// <summary>
    /// 是否为内置的离线演示向量（按字词哈希，只反映字面相似）。
    /// </summary>
    bool IsDemo { get; }

    /// <summary>
    /// 向量检索的相关性门槛（余弦距离）：距离更大的候选视为不相关。
    /// </summary>
    double MaxDistance { get; }

    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}

public static class EmbeddingVector
{
    /// <summary>
    /// 平台统一的向量维度。维度更低的模型补零（不改变余弦相似度），更高的截断（适用于 OpenAI text-embedding-3、
    /// Qwen3-Embedding 等支持 Matryoshka 表示的模型），然后归一化。
    /// </summary>
    public const int Dimensions = 1024;

    public static float[] Fit(ReadOnlySpan<float> source)
    {
        var result = new float[Dimensions];
        source[..Math.Min(source.Length, Dimensions)].CopyTo(result);
        double norm = 0;
        foreach (var value in result)
        {
            norm += value * (double)value;
        }

        if (norm > 0)
        {
            var scale = (float)(1 / Math.Sqrt(norm));
            for (var i = 0; i < result.Length; i++)
            {
                result[i] *= scale;
            }
        }

        return result;
    }

    /// <summary>
    /// pgvector 的文本表示：[0.1,0.2,...]，以 text 参数传入后在 SQL 里转换为 vector。
    /// </summary>
    public static string ToSql(float[] vector)
    {
        var sb = new StringBuilder(vector.Length * 10);
        sb.Append('[');
        for (var i = 0; i < vector.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(vector[i].ToString("G9", CultureInfo.InvariantCulture));
        }

        return sb.Append(']').ToString();
    }
}

/// <summary>
/// 混合检索请求。可见性由当前身份决定：文档全空间可见；记忆只看共享的和自己的。
/// </summary>
public sealed record KnowledgeSearchRequest
{
    public required string Query { get; init; }

    public bool IncludeDocuments { get; init; } = true;

    public bool IncludeMemories { get; init; } = true;

    public int Limit { get; init; } = 8;

    /// <summary>
    /// 额外纳入某个智能体的私有记忆（需要 memory.manage，由调用方检查）。
    /// </summary>
    public Guid? AlsoAgentMemoriesOf { get; init; }
}

public sealed record KnowledgeHit(
    Guid ChunkId,
    KnowledgeSourceKind SourceKind,
    Guid SourceId,
    int Seq,
    string Content,
    double Score,
    bool SemanticMatch,
    bool KeywordMatch);

/// <summary>
/// 混合检索：向量近邻与关键词各取若干候选，按倒数排名融合（RRF）。向量服务不可用时只走关键词。
/// </summary>
public interface IKnowledgeSearch
{
    Task<IReadOnlyList<KnowledgeHit>> SearchAsync(KnowledgeSearchRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// 通知后台索引器有新片段待向量化（进程内信号，事务提交后发出）。
/// </summary>
public interface IKnowledgeIndexSignal
{
    void Notify();
}

public static class ContentHash
{
    public static string Of(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// 记忆去重用：忽略首尾空白、大小写和连续空白的差异。
    /// </summary>
    public static string OfNormalized(string text)
        => Of(string.Join(' ', text.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
}

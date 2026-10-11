using AiNative.Application.Knowledge;
using AiNative.Application.Knowledge.Text;

namespace AiNative.Infrastructure.Knowledge;

/// <summary>
/// 离线演示向量：把分词结果哈希到固定维度（特征哈希），按词频加权后归一化。
/// 只反映字面重合，不理解语义；用于没有配置向量模型时开箱可用，以及测试。
/// </summary>
internal static class DemoEmbedder
{
    public const string ModelId = "demo/hash-v1";

    public static float[] Embed(string text)
    {
        var vector = new float[EmbeddingVector.Dimensions];
        foreach (var group in TextTokenizer.Tokenize(text).GroupBy(t => t, StringComparer.Ordinal))
        {
            var hash = Fnv1a(group.Key);
            var index = (int)(hash % EmbeddingVector.Dimensions);
            var sign = (hash >> 63) == 0 ? 1f : -1f;
            vector[index] += sign * (1 + MathF.Log(group.Count()));
        }

        return EmbeddingVector.Fit(vector);
    }

    /// <summary>
    /// 稳定哈希（FNV-1a 64 位）。不能用 string.GetHashCode：它每个进程随机化，重启后向量就对不上了。
    /// </summary>
    private static ulong Fnv1a(string value)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in value)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }

        return hash;
    }
}

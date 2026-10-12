using AiNative.Domain.Common;

namespace AiNative.Domain.Knowledge;

/// <summary>
/// 知识库文档。保存时同步切成片段（<see cref="KnowledgeChunk"/>），立即可以按关键词检索；
/// 向量由后台补齐。人、智能体和外部 MCP 客户端检索的是同一份知识。
/// </summary>
public sealed class KnowledgeDocument : TenantEntity
{
    public required string Title { get; set; }

    public string Content { get; private set; } = string.Empty;

    /// <summary>
    /// 正文的 SHA-256，用来判断内容是否真的变了（变了才重新切分）。
    /// </summary>
    public string ContentHash { get; private set; } = string.Empty;

    /// <summary>
    /// 上传时的文件名；手动录入为空。
    /// </summary>
    public string? FileName { get; set; }

    public List<string> Tags { get; set; } = [];

    public Actor CreatedBy { get; set; }

    public int ChunkCount { get; private set; }

    /// <summary>
    /// 最近一次切分的时间。
    /// </summary>
    public DateTimeOffset? IndexedAt { get; private set; }

    /// <summary>
    /// 设置正文。返回内容是否发生变化（变化时调用方需要重新切分）。
    /// </summary>
    public bool SetContent(string content, string contentHash)
    {
        if (ContentHash == contentHash)
        {
            return false;
        }

        Content = content;
        ContentHash = contentHash;
        return true;
    }

    public void MarkChunked(int chunkCount, DateTimeOffset now)
    {
        ChunkCount = chunkCount;
        IndexedAt = now;
    }
}

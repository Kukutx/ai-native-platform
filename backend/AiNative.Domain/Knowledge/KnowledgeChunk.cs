using AiNative.Domain.Common;

namespace AiNative.Domain.Knowledge;

public enum KnowledgeSourceKind
{
    Document = 0,
    Memory = 1,
}

/// <summary>
/// 可检索片段：文档切出的段落，或一条记忆。文档和记忆共用一张表，一条 SQL 完成向量 + 关键词混合检索。
/// 归属字段从来源冗余过来，检索时不需要回表判断可见性。
/// 向量列（embedding）由迁移以原生 SQL 创建、由索引器以原生 SQL 写入，不经过 EF 跟踪。
/// </summary>
public sealed class KnowledgeChunk : TenantEntity
{
    public KnowledgeSourceKind SourceKind { get; set; }

    public Guid? DocumentId { get; set; }

    public Guid? MemoryId { get; set; }

    public MemoryScope Scope { get; set; }

    public Guid? OwnerId { get; set; }

    /// <summary>
    /// 片段在文档中的顺序（从 0 开始）。
    /// </summary>
    public int Seq { get; set; }

    public required string Content { get; set; }

    /// <summary>
    /// 分词结果（空格分隔），数据库据此生成 tsvector 做关键词检索。中文按二元组切分，不依赖数据库分词插件。
    /// </summary>
    public required string SearchText { get; set; }

    /// <summary>
    /// 生成向量所用的模型；与当前配置不一致的片段会被后台重新向量化。
    /// </summary>
    public string? EmbeddingModel { get; set; }
}

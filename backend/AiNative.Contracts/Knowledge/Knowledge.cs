using AiNative.Contracts.Common;
using AiNative.Domain.Knowledge;

namespace AiNative.Contracts.Knowledge;

public sealed record KnowledgeDocumentDto(
    Guid Id,
    string Title,
    string? FileName,
    IReadOnlyList<string> Tags,
    int ContentLength,
    int ChunkCount,
    int EmbeddedChunkCount,
    ActorDto CreatedBy,
    DateTimeOffset? IndexedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record KnowledgeChunkDto(Guid Id, int Seq, string Content, bool Embedded);

public sealed record KnowledgeDocumentDetailDto(KnowledgeDocumentDto Document, string Content, IReadOnlyList<KnowledgeChunkDto> Chunks);

public sealed record SaveKnowledgeDocumentRequest
{
    public required string Title { get; init; }

    public required string Content { get; init; }

    public string? FileName { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed record MemoryDto(
    Guid Id,
    MemoryScope Scope,
    ActorDto? Owner,
    string Content,
    IReadOnlyList<string> Tags,
    ActorDto CreatedBy,
    Guid? RunId,
    Guid? WorkItemId,
    int RecallCount,
    DateTimeOffset? LastRecalledAt,
    DateTimeOffset CreatedAt);

public sealed record CreateMemoryRequest
{
    public required string Content { get; init; }

    /// <summary>
    /// 为 true 时写入空间共享记忆，否则只属于自己。
    /// </summary>
    public bool Shared { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>
/// 检索命中的片段。语义命中与关键词命中按倒数排名融合成一个分数。
/// </summary>
public sealed record KnowledgeHitDto(
    KnowledgeSourceKind SourceKind,
    Guid SourceId,
    string Title,
    int Seq,
    string Content,
    double Score,
    bool SemanticMatch,
    bool KeywordMatch,
    MemoryScope? Scope,
    DateTimeOffset CreatedAt);

/// <summary>
/// 检索能力状态：当前向量模型、是否为离线演示向量、待向量化的片段数。
/// </summary>
public sealed record KnowledgeStatusDto(string EmbeddingModel, bool IsDemoEmbedding, int Documents, int Memories, int PendingEmbeddings);

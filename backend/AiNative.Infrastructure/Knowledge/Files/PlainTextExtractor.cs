using AiNative.Application.Knowledge;

namespace AiNative.Infrastructure.Knowledge.Files;

/// <summary>
/// Markdown、纯文本、CSV、JSON 等文本文件：只识别编码，内容原样保留。
/// </summary>
internal sealed class PlainTextExtractor : IKnowledgeFileExtractor
{
    public string Format => "文本";

    public IReadOnlyCollection<string> Extensions { get; } = [".md", ".markdown", ".txt", ".text", ".csv", ".tsv", ".json", ".log", ".xml", ".yaml", ".yml"];

    public ExtractedText Extract(Stream content, CancellationToken cancellationToken) => new(TextDecoding.Decode(content));
}

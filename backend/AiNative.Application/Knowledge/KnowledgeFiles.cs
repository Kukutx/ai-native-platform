using System.Text;
using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Application.Files;
using AiNative.Contracts.Knowledge;
using Microsoft.Extensions.Logging;

namespace AiNative.Application.Knowledge;

/// <summary>
/// 解析上传的文件，抽取正文供用户确认后保存成知识库文档。只做转换，不落库，也不保存原文件。
/// </summary>
[RequireFeature(Features.Knowledge.Manage)]
public sealed record ExtractKnowledgeFileQuery(string FileName, long Length, Stream Content) : IQuery<ExtractedKnowledgeFileDto>;

/// <summary>
/// 某种格式抽出的文本（Markdown）。
/// </summary>
public sealed record ExtractedText(string Content, string? Title = null, int? Pages = null, IReadOnlyList<string>? Warnings = null);

/// <summary>
/// 把一种文件格式转换成 Markdown 文本：标题转成 #，列表转成 -，表格转成 Markdown 表格，便于切分时保留章节路径。
/// 实现依赖解析库，放在基础设施层；按扩展名选择。
/// </summary>
public interface IKnowledgeFileExtractor
{
    /// <summary>给用户看的格式名，例如 PDF、Word。</summary>
    string Format { get; }

    /// <summary>支持的扩展名（小写，带点）。</summary>
    IReadOnlyCollection<string> Extensions { get; }

    /// <param name="content">可定位的流（已整体读入内存）。</param>
    ExtractedText Extract(Stream content, CancellationToken cancellationToken);
}

/// <summary>
/// 文件无法解析：格式不支持、太大、损坏、加密或没有文字。消息直接展示给用户。
/// </summary>
public sealed class KnowledgeFileException(string code, string message) : AppException(code, message);

public static class KnowledgeFiles
{
    public const long MaxFileBytes = 20 * 1024 * 1024;

    /// <summary>
    /// 旧版二进制格式，提示另存为新格式。
    /// </summary>
    private static readonly Dictionary<string, string> LegacyFormats = new(StringComparer.Ordinal)
    {
        [".doc"] = ".docx",
        [".xls"] = ".xlsx",
        [".ppt"] = ".pptx",
        [".wps"] = ".docx",
        [".et"] = ".xlsx",
        [".dps"] = ".pptx",
    };

    public static string UnsupportedMessage(string extension, IEnumerable<string> supported)
    {
        if (LegacyFormats.TryGetValue(extension, out var modern))
        {
            return $"暂不支持旧版 {extension} 文件，请用 Office 或 WPS 另存为 {modern} 后再上传。";
        }

        var name = extension.Length == 0 ? "没有扩展名的文件" : $"{extension} 文件";
        return $"暂不支持{name}。支持的格式：{string.Join("、", supported)}。";
    }

    /// <summary>
    /// 各格式共用的整理：统一换行、去掉控制字符和行尾空白、合并多余空行。
    /// </summary>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var blankLines = 0;
        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = StripControl(rawLine).TrimEnd();
            if (line.Length == 0)
            {
                blankLines++;
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(blankLines > 0 ? "\n\n" : "\n");
            }

            builder.Append(line);
            blankLines = 0;
        }

        return builder.ToString();
    }

    private static string StripControl(string line)
    {
        if (!line.Any(IsDropped))
        {
            return line;
        }

        return string.Concat(line.Where(c => !IsDropped(c)));
    }

    // 制表符保留（表格、代码缩进），其余控制字符、零宽字符和 BOM 去掉。
    private static bool IsDropped(char c)
        => (char.IsControl(c) && c != '\t') || c is '​' or '‌' or '‍' or '⁠' or '﻿' or '­';

    /// <summary>
    /// 超过文档上限时截断，尽量停在段落结尾。
    /// </summary>
    public static (string Content, bool Truncated) Truncate(string content, int maxChars)
    {
        if (content.Length <= maxChars)
        {
            return (content, false);
        }

        var cut = content.LastIndexOf("\n\n", maxChars - 1, StringComparison.Ordinal);
        if (cut < maxChars * 9 / 10)
        {
            cut = maxChars;
        }

        // 不在代理对中间截断。
        if (char.IsHighSurrogate(content[cut - 1]))
        {
            cut--;
        }

        return (content[..cut].TrimEnd(), true);
    }

    public static string TitleFor(string? extracted, string fileName)
    {
        var title = extracted?.Trim();
        // Word 转 PDF 时常把标题写成「Microsoft Word - 文件名.docx」。
        if (title is not null && title.StartsWith("Microsoft Word - ", StringComparison.OrdinalIgnoreCase))
        {
            title = Path.GetFileNameWithoutExtension(title["Microsoft Word - ".Length..]);
        }

        if (string.IsNullOrWhiteSpace(title) || title.Length > 300 || title.Equals("untitled", StringComparison.OrdinalIgnoreCase))
        {
            title = Path.GetFileNameWithoutExtension(fileName).Trim();
        }

        title = string.IsNullOrWhiteSpace(title) ? "未命名文档" : title;
        return title.Length <= 300 ? title : title[..300];
    }
}

internal sealed class ExtractKnowledgeFileQueryHandler(IEnumerable<IKnowledgeFileExtractor> extractors, ILogger<ExtractKnowledgeFileQueryHandler> logger)
    : IRequestHandler<ExtractKnowledgeFileQuery, ExtractedKnowledgeFileDto>
{
    private readonly IReadOnlyList<IKnowledgeFileExtractor> _extractors = [.. extractors];

    public async Task<ExtractedKnowledgeFileDto> HandleAsync(ExtractKnowledgeFileQuery request, CancellationToken cancellationToken)
    {
        var fileName = UploadedFiles.SafeName(request.FileName);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var extractor = _extractors.FirstOrDefault(e => e.Extensions.Contains(extension))
                        ?? throw new KnowledgeFileException("knowledge.file_unsupported",
                            KnowledgeFiles.UnsupportedMessage(extension, _extractors.SelectMany(e => e.Extensions).Order(StringComparer.Ordinal)));

        if (request.Length == 0)
        {
            throw new KnowledgeFileException("knowledge.file_empty", "文件是空的。");
        }

        using var buffer = await UploadedFiles.ReadAsync(
            request.Content,
            request.Length,
            KnowledgeFiles.MaxFileBytes,
            () => new KnowledgeFileException("knowledge.file_too_large", $"文件超过 {KnowledgeFiles.MaxFileBytes / 1024 / 1024} MB，请拆分后再上传。"),
            cancellationToken).ConfigureAwait(false);

        ExtractedText extracted;
        try
        {
            extracted = extractor.Extract(buffer, cancellationToken);
        }
        catch (Exception ex) when (ex is not (KnowledgeFileException or OperationCanceledException))
        {
            // 解析库对畸形文件抛出的异常五花八门，统一告诉用户文件读不了，细节留在日志里。
            logger.LogWarning(ex, "Failed to extract {Format} file {FileName}", extractor.Format, fileName);
            throw new KnowledgeFileException("knowledge.file_unreadable", $"无法解析这个{extractor.Format}文件，可能已损坏或使用了不支持的特性。");
        }

        var content = KnowledgeFiles.Normalize(extracted.Content);
        if (content.Length == 0)
        {
            throw new KnowledgeFileException("knowledge.file_empty", "没有从文件里读到文字。");
        }

        var warnings = extracted.Warnings?.ToList() ?? [];
        var (kept, truncated) = KnowledgeFiles.Truncate(content, KnowledgeLimits.MaxDocumentChars);
        if (truncated)
        {
            warnings.Add($"内容超过 {KnowledgeLimits.MaxDocumentChars / 10_000} 万字，只保留了前面的部分，其余内容请拆成单独的文档。");
        }

        return new ExtractedKnowledgeFileDto(KnowledgeFiles.TitleFor(extracted.Title, fileName), kept, fileName, extractor.Format, extracted.Pages, truncated, warnings);
    }
}

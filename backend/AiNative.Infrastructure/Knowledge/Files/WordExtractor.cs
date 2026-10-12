using System.Text;
using AiNative.Application.Knowledge;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AiNative.Infrastructure.Knowledge.Files;

/// <summary>
/// Word（.docx）：按正文顺序输出段落；标题样式（包括中文版 Word 的「标题 1」和大纲级别）转成 #，
/// 编号和项目符号转成 -，表格转成 Markdown 表格。批注、修订中删除的文字和域代码不输出。
/// </summary>
internal sealed class WordExtractor : IKnowledgeFileExtractor
{
    private const int MaxHeadingLength = 200;

    public string Format => "Word";

    public IReadOnlyCollection<string> Extensions { get; } = [".docx", ".docm"];

    public ExtractedText Extract(Stream content, CancellationToken cancellationToken)
    {
        using var document = OpenXmlFiles.Open(() => WordprocessingDocument.Open(content, false, OpenXmlFiles.Settings), "Word ");
        var main = document.MainDocumentPart;
        var body = main?.Document?.Body;
        if (main is null || body is null)
        {
            throw new KnowledgeFileException("knowledge.file_unreadable", "这个 Word 文件里没有正文。");
        }

        var styles = StyleInfo.Load(main);
        var builder = new StringBuilder();
        AppendBlocks(builder, body.ChildElements, styles, cancellationToken);
        return new ExtractedText(builder.ToString(), OpenXmlFiles.Title(document));
    }

    private static void AppendBlocks(StringBuilder builder, IEnumerable<OpenXmlElement> elements, StyleInfo styles, CancellationToken cancellationToken)
    {
        foreach (var element in elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (element)
            {
                case Paragraph paragraph:
                    AppendParagraph(builder, paragraph, styles);
                    break;
                case Table table:
                    EndList(builder);
                    MarkdownTable.Append(builder, [.. table.Elements<TableRow>().Select(row => (IReadOnlyList<string>)[.. row.Elements<TableCell>().Select(CellText)])]);
                    break;
                case SdtBlock sdt when sdt.SdtContentBlock is { } sdtContent:
                    AppendBlocks(builder, sdtContent.ChildElements, styles, cancellationToken);
                    break;
                case CustomXmlBlock custom:
                    AppendBlocks(builder, custom.ChildElements, styles, cancellationToken);
                    break;
            }
        }
    }

    private static void AppendParagraph(StringBuilder builder, Paragraph paragraph, StyleInfo styles)
    {
        var text = ParagraphText(paragraph).Trim();
        if (text.Length == 0)
        {
            return;
        }

        var properties = paragraph.ParagraphProperties;
        var styleId = properties?.ParagraphStyleId?.Val?.Value;
        var level = properties?.OutlineLevel?.Val?.Value is { } outline && outline < 9 ? outline + 1 : styles.HeadingLevel(styleId);
        if (level is >= 1 and <= 6 && text.Length <= MaxHeadingLength && !text.Contains('\n'))
        {
            EndList(builder);
            builder.Append(new string('#', level.Value)).Append(' ').Append(text).Append("\n\n");
            return;
        }

        if (properties?.NumberingProperties?.NumberingId?.Val?.Value is > 0 || styles.IsList(styleId))
        {
            var indent = properties?.NumberingProperties?.NumberingLevelReference?.Val?.Value ?? 0;
            builder.Append(new string(' ', Math.Min(indent, 4) * 2)).Append("- ").Append(text.Replace("\n", " ", StringComparison.Ordinal)).Append('\n');
            return;
        }

        EndList(builder);
        builder.Append(text).Append("\n\n");
    }

    /// <summary>
    /// 列表项之间只换一行；列表结束后补一个空行，后面的标题和段落才会被当成新的块。
    /// </summary>
    private static void EndList(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] == '\n' && (builder.Length < 2 || builder[^2] != '\n'))
        {
            builder.Append('\n');
        }
    }

    /// <summary>
    /// 段落里的文字：按文档顺序拼接文本、制表符和换行；修订中删除的文字（delText）和域代码（instrText）是别的元素类型，自然不包含。
    /// </summary>
    private static string ParagraphText(OpenXmlElement paragraph)
    {
        var builder = new StringBuilder();
        foreach (var node in paragraph.Descendants())
        {
            switch (node)
            {
                case Text text:
                    builder.Append(text.Text);
                    break;
                case TabChar:
                    builder.Append('\t');
                    break;
                case Break or CarriageReturn:
                    builder.Append('\n');
                    break;
                case NoBreakHyphen:
                    builder.Append('-');
                    break;
            }
        }

        return builder.ToString();
    }

    private static string CellText(TableCell cell)
        => string.Join(" ", cell.Descendants<Paragraph>()
            .Where(p => !p.Ancestors<Paragraph>().Any())
            .Select(p => ParagraphText(p).Trim())
            .Where(t => t.Length > 0));

    /// <summary>
    /// 样式表里的标题级别和列表样式。中文版 Word 的样式 ID 是「1」「2」这类本地化的值，要看样式名（heading 1）
    /// 或样式里的大纲级别，并沿着「基于」链继承。
    /// </summary>
    private sealed class StyleInfo
    {
        private readonly Dictionary<string, Style> _styles;
        private readonly Dictionary<string, int?> _levels = new(StringComparer.Ordinal);

        private StyleInfo(Dictionary<string, Style> styles) => _styles = styles;

        public static StyleInfo Load(MainDocumentPart main)
        {
            var styles = new Dictionary<string, Style>(StringComparer.Ordinal);
            foreach (var style in main.StyleDefinitionsPart?.Styles?.Elements<Style>() ?? [])
            {
                if (style.StyleId?.Value is { } id && style.Type?.Value == StyleValues.Paragraph)
                {
                    styles.TryAdd(id, style);
                }
            }

            return new StyleInfo(styles);
        }

        public int? HeadingLevel(string? styleId)
        {
            if (styleId is null)
            {
                return null;
            }

            if (_levels.TryGetValue(styleId, out var cached))
            {
                return cached;
            }

            int? level = null;
            var current = styleId;
            for (var depth = 0; depth < 10 && current is not null && _styles.TryGetValue(current, out var style); depth++)
            {
                var name = style.StyleName?.Val?.Value?.Trim().ToLowerInvariant() ?? string.Empty;
                if (name is "title" or "标题")
                {
                    level = 1;
                    break;
                }

                if (name.StartsWith("heading ", StringComparison.Ordinal) && int.TryParse(name["heading ".Length..], out var n))
                {
                    level = n;
                    break;
                }

                if (style.StyleParagraphProperties?.OutlineLevel?.Val?.Value is { } outline && outline < 9)
                {
                    level = outline + 1;
                    break;
                }

                current = style.BasedOn?.Val?.Value;
            }

            _levels[styleId] = level;
            return level;
        }

        public bool IsList(string? styleId)
        {
            var current = styleId;
            for (var depth = 0; depth < 10 && current is not null && _styles.TryGetValue(current, out var style); depth++)
            {
                if (style.StyleParagraphProperties?.NumberingProperties?.NumberingId?.Val?.Value is > 0)
                {
                    return true;
                }

                current = style.BasedOn?.Val?.Value;
            }

            return false;
        }
    }
}

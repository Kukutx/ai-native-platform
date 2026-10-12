using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AiNative.Application.Knowledge;
using AiNative.Infrastructure.Files;

namespace AiNative.Infrastructure.Knowledge.Files;

/// <summary>
/// 网页（另存为的 .html）：去掉脚本、样式和标签，标题转成 #、列表项转成 -，块级元素之间分段。
/// 不追求完整的 HTML 解析，目标是让正文和章节结构可检索。
/// </summary>
internal sealed partial class HtmlExtractor : IKnowledgeFileExtractor
{
    public string Format => "网页";

    public IReadOnlyCollection<string> Extensions { get; } = [".html", ".htm"];

    public ExtractedText Extract(Stream content, CancellationToken cancellationToken)
    {
        var html = TextDecoding.Decode(content);
        var title = TitlePattern().Match(html) is { Success: true } match ? Clean(match.Groups[1].Value) : null;
        return new ExtractedText(ToMarkdown(html), string.IsNullOrWhiteSpace(title) ? null : title);
    }

    public static string ToMarkdown(string html)
    {
        var text = DroppedPattern().Replace(html, " ");

        // 预格式文本原样保留，先换成占位符，免得被下面的空白压缩打乱。
        var preserved = new List<string>();
        text = PrePattern().Replace(text, m =>
        {
            preserved.Add(WebUtility.HtmlDecode(TagPattern().Replace(m.Groups[1].Value, string.Empty)).Trim('\n'));
            return $"<p>\u0001{preserved.Count - 1}\u0001</p>";
        });

        // 源码里的换行和缩进不是内容，段落和换行由标签决定。
        text = WhitespacePattern().Replace(text, " ");
        text = TablePattern().Replace(text, m => "\n\n" + Table(m.Groups[1].Value) + "\n\n");
        text = HeadingPattern().Replace(text, m => $"\n\n{new string('#', m.Groups[1].Value[0] - '0')} {Clean(m.Groups[2].Value)}\n\n");
        text = ListItemPattern().Replace(text, "\n- ");
        text = LineBreakPattern().Replace(text, "\n");
        text = BlockEndPattern().Replace(text, "\n\n");
        text = TagPattern().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);

        var builder = new StringBuilder(text.Length);
        foreach (var line in text.Split('\n'))
        {
            builder.Append(SpacePattern().Replace(line, " ").Trim()).Append('\n');
        }

        return PlaceholderPattern().Replace(builder.ToString(), m => preserved[int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)]);
    }

    private static string Table(string table)
    {
        var rows = RowPattern().Matches(table)
            .Select(row => (IReadOnlyList<string>)[.. CellPattern().Matches(row.Groups[1].Value).Select(cell => Clean(LineBreakPattern().Replace(cell.Groups[1].Value, " ")))])
            .ToList();
        var builder = new StringBuilder();
        MarkdownTable.Append(builder, rows);
        return builder.ToString();
    }

    private static string Clean(string fragment) => SpacePattern().Replace(WebUtility.HtmlDecode(TagPattern().Replace(fragment, string.Empty)), " ").Trim();

    [GeneratedRegex(@"<title[^>]*>([\s\S]*?)</title>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex TitlePattern();

    [GeneratedRegex(@"<(script|style|noscript|template|svg|head)\b[^>]*>[\s\S]*?</\1\s*>|<!--[\s\S]*?-->", RegexOptions.IgnoreCase, 5000)]
    private static partial Regex DroppedPattern();

    [GeneratedRegex(@"<pre\b[^>]*>([\s\S]*?)</pre\s*>", RegexOptions.IgnoreCase, 5000)]
    private static partial Regex PrePattern();

    [GeneratedRegex(@"\u0001(\d+)\u0001", RegexOptions.None, 1000)]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"\s+", RegexOptions.None, 5000)]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"<table\b[^>]*>([\s\S]*?)</table\s*>", RegexOptions.IgnoreCase, 5000)]
    private static partial Regex TablePattern();

    [GeneratedRegex(@"<tr\b[^>]*>([\s\S]*?)(?=<tr\b|</tr\s*>|$)", RegexOptions.IgnoreCase, 5000)]
    private static partial Regex RowPattern();

    [GeneratedRegex(@"<t[hd]\b[^>]*>([\s\S]*?)(?=<t[hd]\b|</t[hd]\s*>|</tr\s*>|$)", RegexOptions.IgnoreCase, 5000)]
    private static partial Regex CellPattern();

    [GeneratedRegex(@"<h([1-6])\b[^>]*>([\s\S]*?)</h\1\s*>", RegexOptions.IgnoreCase, 5000)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ListItemPattern();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex LineBreakPattern();

    [GeneratedRegex(@"</(p|div|section|article|header|footer|main|aside|nav|table|tr|ul|ol|dl|dt|dd|blockquote|pre|figure|figcaption)\s*>|<(hr|p|tr)\b[^>]*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex BlockEndPattern();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.None, 1000)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"[ \t 　]+", RegexOptions.None, 1000)]
    private static partial Regex SpacePattern();
}

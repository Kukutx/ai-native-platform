using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AiNative.Application.Knowledge;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Exceptions;

namespace AiNative.Infrastructure.Knowledge.Files;

/// <summary>
/// PDF：读取文字层（不做 OCR）。每页先把字母组成词、再分成文本块并按阅读顺序排列（多栏排版也能按栏读），
/// 去掉每页重复出现在页面边缘的页眉、页脚和页码；字号明显大于正文的短块当作标题输出为 #。
/// 块内的换行是排版造成的，合并成一段：中文之间直接相连，英文之间补空格，行尾连字符去掉。
/// </summary>
internal sealed partial class PdfExtractor : IKnowledgeFileExtractor
{
    private const int MaxPages = 2_000;
    private const int MaxHeadingLength = 80;

    private static readonly ParsingOptions Options = new() { UseLenientParsing = true, SkipMissingFonts = true };

    public string Format => "PDF";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    public ExtractedText Extract(Stream content, CancellationToken cancellationToken)
    {
        PdfDocument document;
        try
        {
            document = PdfDocument.Open(content, Options);
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new KnowledgeFileException("knowledge.file_encrypted", "这个 PDF 设置了打开密码，请先去掉密码再上传。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new KnowledgeFileException("knowledge.file_unreadable", "无法打开这个 PDF 文件，可能已损坏，或者扩展名和实际格式不符。");
        }

        using (document)
        {
            return Extract(document, cancellationToken);
        }
    }

    private static ExtractedText Extract(PdfDocument document, CancellationToken cancellationToken)
    {
        var total = document.NumberOfPages;
        var read = Math.Min(total, MaxPages);
        var warnings = new List<string>();
        var pages = new List<PageBlocks>(read);
        var blank = new List<int>();
        var broken = new List<int>();
        for (var number = 1; number <= read; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var page = document.GetPage(number);
                if (!page.Letters.Any(l => !string.IsNullOrWhiteSpace(l.Value)))
                {
                    blank.Add(number);
                    pages.Add(new PageBlocks([], page.Height));
                    continue;
                }

                var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters).ToList();
                var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
                pages.Add(new PageBlocks([.. UnsupervisedReadingOrderDetector.Instance.Get(blocks)], page.Height));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                broken.Add(number);
                pages.Add(new PageBlocks([], 0));
            }
        }

        if (blank.Count + broken.Count == read)
        {
            throw new KnowledgeFileException("knowledge.file_no_text",
                "这个 PDF 里没有可提取的文字，可能是扫描件或图片。目前还不支持识别图片里的文字（OCR），请上传带文字层的 PDF，或者把内容复制成文本。");
        }

        if (blank.Count > 0)
        {
            warnings.Add($"{PageList(blank)}没有可提取的文字（可能是扫描图片），已跳过。");
        }

        if (broken.Count > 0)
        {
            warnings.Add($"{PageList(broken)}无法解析，已跳过。");
        }

        if (total > read)
        {
            warnings.Add($"PDF 共 {total} 页，只读取了前 {MaxPages} 页。");
        }

        var decorations = Decorations(pages);
        var segments = new List<Segment>();
        foreach (var page in pages)
        {
            var textWidth = page.Blocks.Count == 0 ? 0 : page.Blocks.Max(b => b.BoundingBox.Width);
            foreach (var block in page.Blocks.Where(b => !decorations.Contains(b)))
            {
                segments.AddRange(Segments(block, textWidth).Where(x => !PageNumberPattern().IsMatch(x.Text) && !BulletOnlyPattern().IsMatch(x.Text)));
            }
        }

        var levels = HeadingLevels(segments);
        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            if (levels.TryGetValue(segment, out var level))
            {
                builder.Append(new string('#', level)).Append(' ');
            }

            builder.Append(segment.Text).Append("\n\n");
        }

        var output = builder.ToString();
        if (LooksGarbled(output))
        {
            warnings.Add("部分文字使用的字体没有编码信息，提取出的内容里可能有乱码，请核对后再保存。");
        }

        return new ExtractedText(output, string.IsNullOrWhiteSpace(document.Information.Title) ? null : document.Information.Title, total, warnings);
    }

    private sealed record PageBlocks(IReadOnlyList<TextBlock> Blocks, double Height);

    /// <summary>
    /// 页眉页脚：位于页面上下边缘、在至少一半页面（且不少于 3 页）重复出现的块。比较时把数字看成同一个字符，
    /// 「第 3 页」「2026 年报」这类每页数字不同的也能识别。只看边缘，正文里重复的模板内容不会被误删。
    /// </summary>
    private static HashSet<TextBlock> Decorations(IReadOnlyList<PageBlocks> pages)
    {
        var result = new HashSet<TextBlock>(ReferenceEqualityComparer.Instance);
        var withText = pages.Count(p => p.Blocks.Count > 0);
        if (withText < 3)
        {
            return result;
        }

        var edges = pages
            .SelectMany((page, index) => page.Blocks
                .Where(b => page.Height > 0 && (b.BoundingBox.Bottom > page.Height * 0.9 || b.BoundingBox.Top < page.Height * 0.1))
                .Select(b => (Page: index, Block: b, Key: DigitPattern().Replace(b.Text.Trim(), "#"))))
            .Where(e => e.Key.Length is > 0 and <= 120)
            .ToList();
        foreach (var group in edges.GroupBy(e => e.Key, StringComparer.Ordinal))
        {
            if (group.Select(e => e.Page).Distinct().Count() * 2 >= withText)
            {
                result.UnionWith(group.Select(e => e.Block));
            }
        }

        return result;
    }

    /// <summary>
    /// 一段连续、字号相近的文字。
    /// </summary>
    private sealed record Segment(string Text, double FontSize, int Lines);

    /// <summary>
    /// 把一个文本块拆成字号相近的几段（标题和紧跟的正文常被分进同一个块），每段内的行合并成一段文字：
    /// 明显没写满的行、以项目符号或编号开头的行、窄栏（表格的一列）里的行另起一行，其余按语言补空格或直接相连。
    /// </summary>
    private static IEnumerable<Segment> Segments(TextBlock block, double textWidth)
    {
        var lines = block.TextLines
            .Select(l => (Text: LineText(l), Size: l.Words.SelectMany(w => w.Letters).Select(c => c.PointSize).DefaultIfEmpty(0).Average(), Width: l.BoundingBox.Width))
            .Where(l => l.Text.Length > 0)
            .ToList();
        var narrow = textWidth > 0 && block.BoundingBox.Width < textWidth * 0.35;
        var blockWidth = block.BoundingBox.Width;
        var start = 0;
        for (var i = 1; i <= lines.Count; i++)
        {
            if (i < lines.Count && !SizeChanged(lines[i - 1].Size, lines[i].Size))
            {
                continue;
            }

            var builder = new StringBuilder();
            for (var j = start; j < i; j++)
            {
                var line = lines[j].Text;
                if (j > start)
                {
                    var previous = lines[j - 1];
                    if (narrow || previous.Width < blockWidth * 0.75 || ListItemPattern().IsMatch(line))
                    {
                        builder.Append('\n');
                    }
                    else if (builder[^1] == '-' && builder.Length > 1 && char.IsLetter(builder[^2]) && char.IsLower(line[0]))
                    {
                        builder.Length--;
                    }
                    else if (!IsCjk(builder[^1]) && !IsCjk(line[0]))
                    {
                        builder.Append(' ');
                    }
                }

                builder.Append(line);
            }

            var size = lines.Skip(start).Take(i - start).Average(l => l.Size);
            yield return new Segment(builder.ToString().Trim(), size, i - start);
            start = i;
        }
    }

    private static bool SizeChanged(double a, double b) => a > 0 && b > 0 && Math.Max(a, b) / Math.Min(a, b) > 1.12;

    /// <summary>
    /// 标题：字号明显大于正文（按字数加权的中位数）的短段。按出现过的标题字号从大到小排名，依次是 #、##、###。
    /// </summary>
    private static Dictionary<Segment, int> HeadingLevels(List<Segment> segments)
    {
        var result = new Dictionary<Segment, int>(ReferenceEqualityComparer.Instance);
        var weighted = segments.Where(s => s.FontSize > 0).OrderBy(s => s.FontSize).ToList();
        var total = weighted.Sum(s => s.Text.Length);
        if (total == 0)
        {
            return result;
        }

        double body = 0;
        var seen = 0;
        foreach (var segment in weighted)
        {
            seen += segment.Text.Length;
            if (seen * 2 >= total)
            {
                body = segment.FontSize;
                break;
            }
        }

        var headings = segments
            .Where(s => s.Lines <= 2 && s.Text.Length <= MaxHeadingLength && !s.Text.Contains('\n') && !EndsLikeSentence(s.Text) && s.FontSize >= body * 1.15)
            .ToList();
        var ranks = headings.Select(s => Math.Round(s.FontSize * 2) / 2).Distinct().OrderDescending().ToList();
        foreach (var heading in headings)
        {
            result[heading] = Math.Min(ranks.IndexOf(Math.Round(heading.FontSize * 2) / 2) + 1, 3);
        }

        return result;
    }

    private static bool EndsLikeSentence(string text) => text[^1] is '。' or '，' or '；' or '、' or ',' or ';' or '：' or ':';

    private static string LineText(TextLine line)
    {
        var builder = new StringBuilder();
        foreach (var word in line.Words)
        {
            var text = word.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0 && !(IsCjk(builder[^1]) && IsCjk(text[0])))
            {
                builder.Append(' ');
            }

            builder.Append(text);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 中日韩文字和全角标点：它们之间不加空格。
    /// </summary>
    private static bool IsCjk(char c)
        => c is >= '⺀' and <= '鿿' or >= '가' and <= '힯' or >= '豈' and <= '﫿' or >= '＀' and <= '￯' or >= '　' and <= '〿';

    /// <summary>
    /// 字体缺少 ToUnicode 映射时，提取出的是私有区字符或替换字符。
    /// </summary>
    private static bool LooksGarbled(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        var bad = text.Count(c => c is >= '' and <= '' or '�');
        return bad > 20 && bad > text.Length / 20;
    }

    private static string PageList(List<int> pages)
    {
        var shown = string.Join("、", pages.Take(10).Select(p => p.ToString(CultureInfo.InvariantCulture)));
        return pages.Count > 10 ? $"第 {shown} 等 {pages.Count} 页" : $"第 {shown} 页";
    }

    [GeneratedRegex(@"^[\s\-–—]*(第\s*)?\d{1,4}\s*页?\s*([/／]\s*\d{1,4}\s*页?)?[\s\-–—]*$|^(page\s*)?\d{1,4}(\s*(of|/)\s*\d{1,4})?$", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex PageNumberPattern();

    [GeneratedRegex(@"\d+", RegexOptions.None, 1000)]
    private static partial Regex DigitPattern();

    [GeneratedRegex(@"^[\s•·●○▪■◆◇\-–—*]+$", RegexOptions.None, 1000)]
    private static partial Regex BulletOnlyPattern();

    [GeneratedRegex(@"^([•·●○▪■◆◇\-–—*]|\d{1,3}[.、)）]|[（(]\d{1,3}[)）]|[一二三四五六七八九十]{1,3}、|[（(][一二三四五六七八九十]{1,3}[)）])\s*", RegexOptions.None, 1000)]
    private static partial Regex ListItemPattern();
}

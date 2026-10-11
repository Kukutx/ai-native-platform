using System.Text;

namespace AiNative.Application.Knowledge.Text;

/// <summary>
/// 把文档切成适合检索的片段：按段落累积到上限，超长段落按句子再切；
/// 每个片段带上所在的 Markdown 标题路径，相邻片段保留少量重叠，避免一句话被切断后两边都检索不到。
/// </summary>
public static class TextChunker
{
    public const int DefaultMaxChars = 1000;
    public const int DefaultOverlapChars = 150;

    private static readonly char[] SentenceEnds = ['。', '！', '？', '；', '.', '!', '?', ';', '\n'];

    public static IReadOnlyList<string> Split(string? text, int maxChars = DefaultMaxChars, int overlapChars = DefaultOverlapChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 100);
        ArgumentOutOfRangeException.ThrowIfNegative(overlapChars);
        overlapChars = Math.Min(overlapChars, maxChars / 3);

        var chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return chunks;
        }

        var headings = new List<(int Level, string Title)>();
        var current = new StringBuilder();
        var currentHeading = string.Empty;

        void Flush()
        {
            var body = current.ToString().Trim();
            if (body.Length > 0)
            {
                chunks.Add(currentHeading.Length == 0 ? body : $"{currentHeading}\n{body}");
            }

            current.Clear();
        }

        foreach (var block in Blocks(text))
        {
            if (TryParseHeading(block, out var level, out var title))
            {
                Flush();
                headings.RemoveAll(h => h.Level >= level);
                headings.Add((level, title));
                currentHeading = string.Join(" > ", headings.Select(h => h.Title));
                continue;
            }

            var budget = maxChars - currentHeading.Length - 1;
            foreach (var piece in SplitLong(block, budget))
            {
                if (current.Length > 0 && current.Length + piece.Length + 2 > budget)
                {
                    var tail = Overlap(current.ToString(), overlapChars);
                    Flush();
                    if (tail.Length > 0 && tail.Length + piece.Length + 2 <= budget)
                    {
                        current.Append(tail);
                    }
                }

                if (current.Length > 0)
                {
                    current.Append("\n\n");
                }

                current.Append(piece);
            }
        }

        Flush();
        return chunks;
    }

    private static IEnumerable<string> Blocks(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var paragraph = new StringBuilder();
        foreach (var line in normalized.Split('\n'))
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0 || TryParseHeading(trimmed, out _, out _))
            {
                if (paragraph.Length > 0)
                {
                    yield return paragraph.ToString();
                    paragraph.Clear();
                }

                if (trimmed.Length > 0)
                {
                    yield return trimmed;
                }

                continue;
            }

            if (paragraph.Length > 0)
            {
                paragraph.Append('\n');
            }

            paragraph.Append(trimmed);
        }

        if (paragraph.Length > 0)
        {
            yield return paragraph.ToString();
        }
    }

    private static bool TryParseHeading(string line, out int level, out string title)
    {
        level = 0;
        while (level < line.Length && level < 6 && line[level] == '#')
        {
            level++;
        }

        if (level > 0 && level < line.Length && line[level] == ' ')
        {
            title = line[(level + 1)..].Trim();
            return title.Length > 0;
        }

        title = string.Empty;
        return false;
    }

    /// <summary>
    /// 超长段落按句末标点切开；单句仍超长时硬切。
    /// </summary>
    private static IEnumerable<string> SplitLong(string block, int maxChars)
    {
        maxChars = Math.Max(maxChars, 50);
        if (block.Length <= maxChars)
        {
            yield return block;
            yield break;
        }

        var start = 0;
        while (start < block.Length)
        {
            var remaining = block.Length - start;
            if (remaining <= maxChars)
            {
                yield return block[start..].Trim();
                yield break;
            }

            var window = block.AsSpan(start, maxChars);
            var cut = window.LastIndexOfAny(SentenceEnds);
            var length = cut >= maxChars / 3 ? cut + 1 : maxChars;
            var piece = block.Substring(start, length).Trim();
            if (piece.Length > 0)
            {
                yield return piece;
            }

            start += length;
        }
    }

    /// <summary>
    /// 取上一片段末尾的若干字符作为重叠，尽量从句子开头开始。
    /// </summary>
    private static string Overlap(string previous, int overlapChars)
    {
        if (overlapChars == 0 || previous.Length <= overlapChars)
        {
            return string.Empty;
        }

        var tail = previous[^overlapChars..];
        var sentenceStart = tail.IndexOfAny(SentenceEnds);
        return sentenceStart >= 0 && sentenceStart < tail.Length - 1 ? tail[(sentenceStart + 1)..].Trim() : tail.Trim();
    }
}

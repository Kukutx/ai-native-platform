using System.Globalization;
using System.Text;

namespace AiNative.Application.Knowledge.Text;

/// <summary>
/// 检索用分词：中日韩文字切成相邻二元组（单字成段时保留单字），其他文字按字母数字连续段切成小写词。
/// 不依赖数据库分词插件，关键词检索与演示向量都用它，结果稳定可测。
/// 全角字母数字和标点先折叠成半角（项目开启了 InvariantGlobalization，string.Normalize 不处理非 ASCII，所以手写）。
/// </summary>
public static class TextTokenizer
{
    private const int MaxWordLength = 40;

    public static IReadOnlyList<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return tokens;
        }

        var word = new StringBuilder();
        var cjkRun = new StringBuilder();

        foreach (var original in text.EnumerateRunes())
        {
            var rune = ToHalfWidth(original);
            if (IsCjk(rune))
            {
                FlushWord(word, tokens);
                cjkRun.Append(rune.ToString());
            }
            else if (Rune.IsLetterOrDigit(rune))
            {
                FlushCjk(cjkRun, tokens);
                word.Append(Rune.ToLowerInvariant(rune).ToString());
            }
            else
            {
                FlushWord(word, tokens);
                FlushCjk(cjkRun, tokens);
            }
        }

        FlushWord(word, tokens);
        FlushCjk(cjkRun, tokens);
        return tokens;
    }

    /// <summary>
    /// 去重后的分词，用于生成 tsvector（数据库直接把每个元素当作词位，不再做任何处理）。
    /// </summary>
    public static string ToSearchText(string? text, int maxTokens = 4000)
        => string.Join(' ', Tokenize(text).Distinct(StringComparer.Ordinal).Take(maxTokens));

    /// <summary>
    /// 把查询转换成 tsquery 文本：各词以 OR 连接，词位用单引号包起来，按字面匹配。没有可用的词时返回 null。
    /// </summary>
    public static string? ToTsQuery(string? query, int maxTokens = 64)
    {
        var tokens = Tokenize(query).Distinct(StringComparer.Ordinal).Take(maxTokens).ToList();
        return tokens.Count == 0 ? null : string.Join(" | ", tokens.Select(t => $"'{t.Replace("'", "''", StringComparison.Ordinal)}'"));
    }

    private static Rune ToHalfWidth(Rune rune) => rune.Value switch
    {
        >= 0xFF01 and <= 0xFF5E => new Rune(rune.Value - 0xFEE0),
        0x3000 => new Rune(' '),
        _ => rune,
    };

    private static void FlushWord(StringBuilder word, List<string> tokens)
    {
        if (word.Length == 0)
        {
            return;
        }

        tokens.Add(word.Length > MaxWordLength ? word.ToString(0, MaxWordLength) : word.ToString());
        word.Clear();
    }

    private static void FlushCjk(StringBuilder run, List<string> tokens)
    {
        if (run.Length == 0)
        {
            return;
        }

        var chars = StringInfo.GetTextElementEnumerator(run.ToString());
        var elements = new List<string>();
        while (chars.MoveNext())
        {
            elements.Add(chars.GetTextElement());
        }

        if (elements.Count == 1)
        {
            tokens.Add(elements[0]);
        }
        else
        {
            for (var i = 0; i < elements.Count - 1; i++)
            {
                tokens.Add(elements[i] + elements[i + 1]);
            }
        }

        run.Clear();
    }

    private static bool IsCjk(Rune rune)
    {
        var value = rune.Value;
        return value is >= 0x4E00 and <= 0x9FFF // 中日韩统一表意文字
            or >= 0x3400 and <= 0x4DBF // 扩展 A
            or >= 0x20000 and <= 0x2EBEF // 扩展 B–F
            or >= 0xF900 and <= 0xFAFF // 兼容表意文字
            or >= 0x3040 and <= 0x30FF // 平假名、片假名
            or >= 0xAC00 and <= 0xD7AF; // 谚文音节
    }
}

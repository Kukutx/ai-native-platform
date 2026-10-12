using System.Text;

namespace AiNative.Infrastructure.Knowledge.Files;

/// <summary>
/// 把表格行写成 Markdown 表格：第一行作表头，单元格里的换行和竖线转义，去掉整列为空的尾部列。
/// </summary>
internal static class MarkdownTable
{
    public static void Append(StringBuilder builder, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var width = 0;
        foreach (var row in rows)
        {
            for (var i = row.Count - 1; i >= 0; i--)
            {
                if (!string.IsNullOrWhiteSpace(row[i]))
                {
                    width = Math.Max(width, i + 1);
                    break;
                }
            }
        }

        var nonEmpty = rows.Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        if (width == 0 || nonEmpty.Count == 0)
        {
            return;
        }

        // 只有一列的「表格」多半是排版用的框，按段落输出更好读。
        if (width == 1)
        {
            foreach (var row in nonEmpty)
            {
                builder.Append(row[0].Trim()).Append("\n\n");
            }

            return;
        }

        AppendRow(builder, nonEmpty[0], width);
        builder.Append('|');
        for (var i = 0; i < width; i++)
        {
            builder.Append(" --- |");
        }

        builder.Append('\n');
        foreach (var row in nonEmpty.Skip(1))
        {
            AppendRow(builder, row, width);
        }

        builder.Append('\n');
    }

    private static void AppendRow(StringBuilder builder, IReadOnlyList<string> row, int width)
    {
        builder.Append('|');
        for (var i = 0; i < width; i++)
        {
            var cell = i < row.Count ? row[i] : string.Empty;
            builder.Append(' ').Append(Escape(cell)).Append(" |");
        }

        builder.Append('\n');
    }

    private static string Escape(string cell)
        => cell.Trim().Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ').Replace("|", "\\|", StringComparison.Ordinal);
}

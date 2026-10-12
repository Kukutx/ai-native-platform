using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace AiNative.Infrastructure.Files;

/// <summary>
/// 读取 Excel 工作簿：可见的工作表、按列号展开的行，单元格转成文本（公式取缓存的计算结果，日期格式的数字转成日期）。
/// </summary>
internal sealed class SpreadsheetCells
{
    // Excel 内置的日期时间数字格式编号。
    private static readonly HashSet<uint> BuiltInDateFormats = [14, 15, 16, 17, 18, 19, 20, 21, 22, 27, 30, 36, 45, 46, 47, 50, 57];

    private readonly List<string> _sharedStrings;
    private readonly HashSet<uint> _dateStyles;

    public SpreadsheetCells(WorkbookPart workbook)
    {
        _sharedStrings = workbook.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(i => i.InnerText).ToList() ?? [];
        _dateStyles = DateStyleIndexes(workbook);
    }

    /// <summary>
    /// 可见的工作表（隐藏和非常隐藏的跳过），按工作簿里的顺序。
    /// </summary>
    public static IEnumerable<(string Name, WorksheetPart Part)> VisibleSheets(WorkbookPart workbook)
    {
        var index = 0;
        foreach (var sheet in workbook.Workbook?.Sheets?.Elements<Sheet>() ?? [])
        {
            index++;
            if ((sheet.State?.Value is { } state && state != SheetStateValues.Visible)
                || sheet.Id?.Value is not { } relationshipId
                || workbook.GetPartById(relationshipId) is not WorksheetPart part)
            {
                continue;
            }

            yield return (sheet.Name?.Value ?? $"工作表 {index}", part);
        }
    }

    /// <summary>
    /// 一行的单元格文本，按列号补齐中间的空单元格。超过 <paramref name="maxColumns"/> 的列不返回，有内容时 <paramref name="overflow"/> 为 true。
    /// </summary>
    public List<string> RowText(Row row, int maxColumns, out bool overflow)
    {
        overflow = false;
        var cells = new List<string>();
        foreach (var cell in row.Elements<Cell>())
        {
            var column = ColumnIndex(cell.CellReference?.Value) ?? cells.Count;
            if (column >= maxColumns)
            {
                overflow |= !string.IsNullOrWhiteSpace(CellText(cell));
                continue;
            }

            while (cells.Count < column)
            {
                cells.Add(string.Empty);
            }

            cells.Add(CellText(cell));
        }

        return cells;
    }

    public string CellText(Cell cell)
    {
        var raw = cell.CellValue?.Text;
        var type = cell.DataType?.Value;
        if (type == CellValues.SharedString)
        {
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < _sharedStrings.Count ? _sharedStrings[index] : string.Empty;
        }

        if (type == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? string.Empty;
        }

        if (type == CellValues.Boolean)
        {
            return raw == "1" ? "TRUE" : "FALSE";
        }

        if (raw is null)
        {
            return string.Empty;
        }

        if ((type is null || type == CellValues.Number)
            && cell.StyleIndex?.Value is { } style && _dateStyles.Contains(style)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 0 and < 2_958_466)
        {
            var date = DateTime.FromOADate(serial);
            return date.TimeOfDay == TimeSpan.Zero ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        // 浮点数按 15 位有效数字输出，避免 0.1+0.2 这类存储误差（0.30000000000000004）出现在正文里。
        return (type is null || type == CellValues.Number) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number.ToString("G15", CultureInfo.InvariantCulture)
            : raw;
    }

    /// <summary>
    /// 哪些单元格样式是日期：内置日期格式，或自定义格式里有年月日（排除引号里的文字和颜色等方括号段）。
    /// </summary>
    private static HashSet<uint> DateStyleIndexes(WorkbookPart workbook)
    {
        var stylesheet = workbook.WorkbookStylesPart?.Stylesheet;
        var custom = stylesheet?.NumberingFormats?.Elements<NumberingFormat>()
            .Where(f => f.NumberFormatId?.Value is not null)
            .ToDictionary(f => f.NumberFormatId!.Value, f => f.FormatCode?.Value ?? string.Empty) ?? [];
        var result = new HashSet<uint>();
        uint index = 0;
        foreach (var format in stylesheet?.CellFormats?.Elements<CellFormat>() ?? [])
        {
            var id = format.NumberFormatId?.Value ?? 0;
            if (BuiltInDateFormats.Contains(id) || (custom.TryGetValue(id, out var code) && LooksLikeDate(code)))
            {
                result.Add(index);
            }

            index++;
        }

        return result;
    }

    private static bool LooksLikeDate(string code)
    {
        var plain = new StringBuilder(code.Length);
        var quoted = false;
        var bracket = false;
        foreach (var c in code)
        {
            switch (c)
            {
                case '"':
                    quoted = !quoted;
                    break;
                case '[' when !quoted:
                    bracket = true;
                    break;
                case ']' when !quoted:
                    bracket = false;
                    break;
                default:
                    if (!quoted && !bracket)
                    {
                        plain.Append(char.ToLowerInvariant(c));
                    }

                    break;
            }
        }

        var text = plain.ToString();
        return text.Contains('y') || text.Contains('d') || text.Contains("年", StringComparison.Ordinal) || (text.Contains('m') && text.Contains('h'));
    }

    /// <summary>
    /// 单元格引用（如 C12）里的列号，从 0 开始。
    /// </summary>
    private static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        var column = 0;
        var letters = 0;
        foreach (var c in reference)
        {
            if (c is >= 'A' and <= 'Z')
            {
                column = (column * 26) + (c - 'A' + 1);
                letters++;
            }
            else
            {
                break;
            }
        }

        return letters is 0 or > 3 ? null : column - 1;
    }
}

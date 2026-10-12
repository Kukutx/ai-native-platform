using System.Text;
using AiNative.Application.Files;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace AiNative.Infrastructure.Files;

/// <summary>
/// 读取 CSV / TSV（RFC 4180：引号包住的字段里可以有分隔符、换行，"" 表示一个引号）和 Excel（第一个有内容的可见工作表）。
/// CSV 的编码自动识别（UTF-8 / GBK），分隔符按表头行里出现最多的逗号、制表符或分号判断。
/// </summary>
internal sealed class TabularFileReader : ITabularFileReader
{
    public IReadOnlyCollection<string> Extensions { get; } = [".csv", ".tsv", ".txt", ".xlsx", ".xlsm"];

    public TabularData Read(string fileName, Stream content, TabularLimits limits, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var (format, rows) = extension switch
        {
            ".xlsx" or ".xlsm" => ("Excel", ReadSpreadsheet(content, limits, cancellationToken)),
            ".csv" or ".tsv" or ".txt" => ("CSV", ReadDelimited(TextDecoding.Decode(content), extension == ".tsv" ? '\t' : null, limits, cancellationToken)),
            _ => throw new TabularFileException("file.unsupported", Unsupported(extension)),
        };

        if (rows.Count == 0)
        {
            throw new TabularFileException("file.empty", "文件里没有内容。");
        }

        var header = rows[0];
        var width = rows.Max(r => r.Cells.Count);
        var headers = Enumerable.Range(0, width)
            .Select(i => header.Cell(i).Trim() is { Length: > 0 } text ? text : $"第 {i + 1} 列")
            .ToList();
        if (rows.Count == 1)
        {
            throw new TabularFileException("file.empty", "文件里只有表头，没有数据行。");
        }

        return new TabularData(format, headers, rows.Skip(1).ToList());
    }

    private static string Unsupported(string extension) => extension switch
    {
        ".xls" or ".et" => $"暂不支持旧版 {extension} 文件，请用 Excel 或 WPS 另存为 .xlsx 或 .csv 后再上传。",
        "" => "请上传 .csv 或 .xlsx 文件。",
        _ => $"暂不支持 {extension} 文件，请上传 .csv 或 .xlsx 文件。",
    };

    private static List<TabularRow> ReadSpreadsheet(Stream content, TabularLimits limits, CancellationToken cancellationToken)
    {
        using var document = OpenXmlFiles.Open(
            () => SpreadsheetDocument.Open(content, false, OpenXmlFiles.Settings),
            () => new TabularFileException("file.unreadable", "无法打开这个 Excel 文件，可能已损坏、加了密码，或者扩展名和实际格式不符。"));
        var workbook = document.WorkbookPart ?? throw new TabularFileException("file.unreadable", "这个 Excel 文件里没有工作表。");
        var cells = new SpreadsheetCells(workbook);
        foreach (var (_, part) in SpreadsheetCells.VisibleSheets(workbook))
        {
            var rows = new List<TabularRow>();
            foreach (var row in part.Worksheet?.Descendants<Row>() ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = cells.RowText(row, limits.MaxColumns, out var overflow);
                if (overflow)
                {
                    throw TooManyColumns(limits);
                }

                if (text.All(string.IsNullOrWhiteSpace))
                {
                    continue;
                }

                Add(rows, new TabularRow((int)(row.RowIndex?.Value ?? (uint)(rows.Count + 1)), text), limits);
            }

            // 只读第一个有内容的工作表；常见的「说明」「下拉选项」放在后面的表里。
            if (rows.Count > 0)
            {
                return rows;
            }
        }

        return [];
    }

    /// <param name="delimiter">为空时按表头行判断。</param>
    internal static List<TabularRow> ReadDelimited(string text, char? delimiter, TabularLimits limits, CancellationToken cancellationToken)
    {
        var separator = delimiter ?? DetectDelimiter(text);
        var rows = new List<TabularRow>();
        var cells = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var line = 1;
        var rowStart = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    field.Append(c);
                }

                continue;
            }

            if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == separator)
            {
                cells.Add(field.ToString());
                field.Clear();
                if (cells.Count > limits.MaxColumns)
                {
                    throw TooManyColumns(limits);
                }
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                cells.Add(field.ToString());
                field.Clear();
                EndRow(rows, cells, rowStart, limits, cancellationToken);
                cells = [];
                line++;
                rowStart = line;
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || cells.Count > 0)
        {
            cells.Add(field.ToString());
            EndRow(rows, cells, rowStart, limits, cancellationToken);
        }

        return rows;
    }

    private static void EndRow(List<TabularRow> rows, List<string> cells, int number, TabularLimits limits, CancellationToken cancellationToken)
    {
        if (cells.Count > limits.MaxColumns)
        {
            throw TooManyColumns(limits);
        }

        if (cells.All(string.IsNullOrWhiteSpace))
        {
            return;
        }

        if (rows.Count % 1000 == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        Add(rows, new TabularRow(number, cells), limits);
    }

    private static void Add(List<TabularRow> rows, TabularRow row, TabularLimits limits)
    {
        // 第一行是表头，不计入数据行数。
        if (rows.Count > limits.MaxRows)
        {
            throw new TabularFileException("file.too_many_rows", $"表格超过 {limits.MaxRows} 行，请拆分成多个文件导入。");
        }

        rows.Add(row);
    }

    /// <summary>
    /// 第一行（引号外）里逗号、制表符、分号哪个最多就用哪个；都没有时按逗号（只有一列）。
    /// </summary>
    private static char DetectDelimiter(string text)
    {
        int comma = 0, tab = 0, semicolon = 0;
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted)
            {
                if (c is '\n' or '\r')
                {
                    break;
                }

                switch (c)
                {
                    case ',':
                        comma++;
                        break;
                    case '\t':
                        tab++;
                        break;
                    case ';':
                        semicolon++;
                        break;
                }
            }
        }

        return tab > comma && tab >= semicolon ? '\t' : semicolon > comma ? ';' : ',';
    }

    private static TabularFileException TooManyColumns(TabularLimits limits)
        => new("file.too_many_columns", $"表格超过 {limits.MaxColumns} 列，请删掉不需要的列后再上传。");
}

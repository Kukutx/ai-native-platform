using System.Text;
using AiNative.Application.Knowledge;
using AiNative.Infrastructure.Files;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace AiNative.Infrastructure.Knowledge.Files;

/// <summary>
/// Excel（.xlsx）：每个可见工作表输出为「## 表名」加一张 Markdown 表格，第一行有内容的行作表头。
/// 公式取缓存的计算结果，日期格式的数字转成日期。行列过多时截断并提示。
/// </summary>
internal sealed class SpreadsheetExtractor : IKnowledgeFileExtractor
{
    private const int MaxRowsPerSheet = 5_000;
    private const int MaxColumns = 50;

    public string Format => "Excel";

    public IReadOnlyCollection<string> Extensions { get; } = [".xlsx", ".xlsm"];

    public ExtractedText Extract(Stream content, CancellationToken cancellationToken)
    {
        using var document = KnowledgeOpenXml.Open(() => SpreadsheetDocument.Open(content, false, OpenXmlFiles.Settings), " Excel ");
        var workbook = document.WorkbookPart;
        if (workbook?.Workbook?.Sheets?.Elements<Sheet>().Any() != true)
        {
            throw new KnowledgeFileException("knowledge.file_unreadable", "这个 Excel 文件里没有工作表。");
        }

        var cells = new SpreadsheetCells(workbook);
        var builder = new StringBuilder();
        var warnings = new List<string>();
        var count = 0;
        foreach (var (name, part) in SpreadsheetCells.VisibleSheets(workbook))
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
            var rows = new List<IReadOnlyList<string>>();
            var truncatedRows = false;
            var truncatedColumns = false;
            foreach (var row in part.Worksheet?.Descendants<Row>() ?? [])
            {
                if (rows.Count >= MaxRowsPerSheet)
                {
                    truncatedRows = true;
                    break;
                }

                rows.Add(cells.RowText(row, MaxColumns, out var overflow));
                truncatedColumns |= overflow;
            }

            if (!rows.Any(r => r.Any(c => !string.IsNullOrWhiteSpace(c))))
            {
                continue;
            }

            builder.Append("## ").Append(name).Append("\n\n");
            MarkdownTable.Append(builder, rows);
            if (truncatedRows)
            {
                warnings.Add($"工作表「{name}」超过 {MaxRowsPerSheet} 行，只保留了前 {MaxRowsPerSheet} 行。");
            }

            if (truncatedColumns)
            {
                warnings.Add($"工作表「{name}」超过 {MaxColumns} 列，后面的列没有导入。");
            }
        }

        return new ExtractedText(builder.ToString(), OpenXmlFiles.Title(document), count, warnings);
    }
}

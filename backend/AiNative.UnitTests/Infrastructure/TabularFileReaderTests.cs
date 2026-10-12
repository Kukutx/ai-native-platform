using System.Text;
using AiNative.Application.Files;
using AiNative.Infrastructure.Files;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace AiNative.UnitTests.Infrastructure;

public sealed class TabularFileReaderTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly TabularLimits Limits = new(100, 20);

    [Fact]
    public void Csv_follows_quoting_rules_and_keeps_file_line_numbers()
    {
        var data = Read("客户.csv", "名称,备注,标签\r\n\"星火, 科技\",\"第一行\n第二行\",\"说 \"\"重点\"\"\"\r\n\r\n云帆,,\r\n");

        data.Format.Should().Be("CSV");
        data.Headers.Should().Equal("名称", "备注", "标签");
        data.Rows.Should().HaveCount(2);
        data.Rows[0].Cells.Should().Equal("星火, 科技", "第一行\n第二行", "说 \"重点\"");
        data.Rows[0].Number.Should().Be(2);
        // 引号里的换行占了一行，空行跳过但仍计入行号。
        data.Rows[1].Number.Should().Be(5);
        data.Rows[1].Cell(2).Should().BeEmpty();
        data.Rows[1].Cell(9).Should().BeEmpty();
    }

    [Theory]
    [InlineData("名称;电话\n星火;010\n")]
    [InlineData("名称\t电话\n星火\t010\n")]
    public void Delimiter_is_detected_from_the_header(string text)
    {
        var data = Read("客户.csv", text);

        data.Headers.Should().Equal("名称", "电话");
        data.Rows.Single().Cells.Should().Equal("星火", "010");
    }

    [Fact]
    public void Gbk_files_from_excel_are_decoded_and_blank_headers_get_names()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding("GB18030").GetBytes("姓名,,邮箱\n王经理,总监,wang@example.com\n");

        var data = new TabularFileReader().Read("联系人.csv", new MemoryStream(bytes), Limits, Ct);

        data.Headers.Should().Equal("姓名", "第 2 列", "邮箱");
        data.Rows.Single().Cell(0).Should().Be("王经理");
    }

    [Fact]
    public void Limits_and_empty_files_are_reported()
    {
        var reader = new TabularFileReader();
        var tooMany = "名称\n" + string.Concat(Enumerable.Range(0, 101).Select(i => $"客户{i}\n"));

        Problem(() => Read("多.csv", tooMany)).Should().Be("file.too_many_rows");
        Read("刚好.csv", "名称\n" + string.Concat(Enumerable.Range(0, 100).Select(i => $"客户{i}\n"))).Rows.Should().HaveCount(100);
        Problem(() => Read("宽.csv", string.Join(',', Enumerable.Range(0, 21)))).Should().Be("file.too_many_columns");
        Problem(() => Read("表头.csv", "名称,电话\n\n")).Should().Be("file.empty");
        Problem(() => Read("空.csv", "\n,\n")).Should().Be("file.empty");
        Problem(() => reader.Read("旧.xls", new MemoryStream([1, 2]), Limits, Ct)).Should().Be("file.unsupported");
        Problem(() => reader.Read("坏.xlsx", new MemoryStream(Encoding.ASCII.GetBytes("PK no zip")), Limits, Ct)).Should().Be("file.unreadable");
    }

    [Fact]
    public void Excel_uses_the_first_visible_sheet_with_content()
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new S.Workbook();
            var styles = workbook.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = new S.Stylesheet(new S.CellFormats(new S.CellFormat { NumberFormatId = 0 }, new S.CellFormat { NumberFormatId = 14, ApplyNumberFormat = true }));
            var hidden = Sheet(workbook, new S.Row(Text("A1", "隐藏")));
            var empty = Sheet(workbook);
            var data = Sheet(
                workbook,
                new S.Row(Text("A1", "名称"), Text("C1", "成立日期")) { RowIndex = 1 },
                new S.Row(Text("A3", "星火科技"), new S.Cell { CellReference = "B3", CellValue = new S.CellValue("12") }, new S.Cell { CellReference = "C3", StyleIndex = 1, CellValue = new S.CellValue("46082") }) { RowIndex = 3 });
            workbook.Workbook.AppendChild(new S.Sheets(
                new S.Sheet { Name = "草稿", SheetId = 1, Id = workbook.GetIdOfPart(hidden), State = S.SheetStateValues.Hidden },
                new S.Sheet { Name = "说明", SheetId = 2, Id = workbook.GetIdOfPart(empty) },
                new S.Sheet { Name = "客户", SheetId = 3, Id = workbook.GetIdOfPart(data) }));
        }

        stream.Position = 0;
        var result = new TabularFileReader().Read("客户.xlsx", stream, Limits, Ct);

        result.Format.Should().Be("Excel");
        result.Headers.Should().Equal("名称", "第 2 列", "成立日期");
        result.Rows.Single().Number.Should().Be(3);
        result.Rows.Single().Cells.Should().Equal("星火科技", "12", "2026-03-01");
    }

    private static TabularData Read(string fileName, string text)
        => new TabularFileReader().Read(fileName, new MemoryStream(Encoding.UTF8.GetBytes(text)), Limits, Ct);

    private static string Problem(Action read)
    {
        var exception = Assert.Throws<TabularFileException>(read);
        return exception.Code;
    }

    private static WorksheetPart Sheet(WorkbookPart workbook, params S.Row[] rows)
    {
        var part = workbook.AddNewPart<WorksheetPart>();
        part.Worksheet = new S.Worksheet(new S.SheetData(rows.Cast<OpenXmlElement>()));
        return part;
    }

    private static S.Cell Text(string reference, string text)
        => new() { CellReference = reference, DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text(text)) };
}

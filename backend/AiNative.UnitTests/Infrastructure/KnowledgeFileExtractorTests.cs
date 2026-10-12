using System.Text;
using AiNative.Application.Knowledge;
using AiNative.Infrastructure.Knowledge.Files;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace AiNative.UnitTests.Infrastructure;

public sealed class KnowledgeFileExtractorTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public void Word_headings_lists_and_tables_become_markdown()
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            document.PackageProperties.Title = "差旅制度";
            var main = document.AddMainDocumentPart();
            var styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new W.Styles(
                ParagraphStyle("Heading1", "heading 1"),
                // 中文版 Word 的样式 ID 是本地化的「2」，名称仍是 heading 2。
                ParagraphStyle("2", "heading 2"),
                ParagraphStyle("Quote", "Quote"));
            main.Document = new W.Document(new W.Body(
                Paragraph("总则", "Heading1"),
                Paragraph("本制度适用于全体员工。"),
                Paragraph("交通", "2"),
                ListItem("高铁二等座"),
                ListItem("飞机经济舱"),
                new W.Paragraph(new W.Run(new W.Text("保留的文字")), new W.DeletedRun(new W.Run(new W.DeletedText("删掉的文字")))),
                new W.Table(
                    Row("城市", "标准"),
                    Row("一线 | 省会", "500 元"))));
        }

        stream.Position = 0;
        var result = new WordExtractor().Extract(stream, Ct);

        result.Title.Should().Be("差旅制度");
        result.Content.Should().Contain("# 总则\n\n本制度适用于全体员工。");
        result.Content.Should().Contain("## 交通\n\n- 高铁二等座\n- 飞机经济舱\n\n保留的文字");
        result.Content.Should().NotContain("删掉的文字");
        result.Content.Should().Contain("| 城市 | 标准 |\n| --- | --- |\n| 一线 \\| 省会 | 500 元 |");
    }

    [Fact]
    public void Excel_sheets_become_tables_with_dates_and_cached_formula_values()
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new S.Workbook();
            var strings = workbook.AddNewPart<SharedStringTablePart>();
            strings.SharedStringTable = new S.SharedStringTable(
                new S.SharedStringItem(new S.Text("产品")),
                new S.SharedStringItem(new S.Text("单价")),
                new S.SharedStringItem(new S.Text("上架日期")),
                new S.SharedStringItem(new S.Text("无线鼠标")));
            var styles = workbook.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = new S.Stylesheet(new S.CellFormats(new S.CellFormat { NumberFormatId = 0 }, new S.CellFormat { NumberFormatId = 14, ApplyNumberFormat = true }));

            var visible = workbook.AddNewPart<WorksheetPart>();
            visible.Worksheet = new S.Worksheet(new S.SheetData(
                new S.Row(Shared("A1", 0), Shared("B1", 1), Shared("D1", 2)),
                new S.Row(
                    Shared("A2", 3),
                    new S.Cell { CellReference = "B2", CellFormula = new S.CellFormula("C9*2"), CellValue = new S.CellValue("89.5") },
                    new S.Cell { CellReference = "D2", StyleIndex = 1, CellValue = new S.CellValue("46082") })));
            var hidden = workbook.AddNewPart<WorksheetPart>();
            hidden.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(new S.Cell { CellReference = "A1", DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text("隐藏的数据")) })));
            workbook.Workbook.AppendChild(new S.Sheets(
                new S.Sheet { Name = "产品", SheetId = 1, Id = workbook.GetIdOfPart(visible) },
                new S.Sheet { Name = "草稿", SheetId = 2, Id = workbook.GetIdOfPart(hidden), State = S.SheetStateValues.Hidden }));
        }

        stream.Position = 0;
        var result = new SpreadsheetExtractor().Extract(stream, Ct);

        result.Content.Should().Be("## 产品\n\n| 产品 | 单价 |  | 上架日期 |\n| --- | --- | --- | --- |\n| 无线鼠标 | 89.5 |  | 2026-03-01 |\n\n");
        result.Content.Should().NotContain("隐藏的数据");
        result.Pages.Should().Be(1);
    }

    [Fact]
    public void PowerPoint_slides_use_titles_bullets_tables_and_notes()
    {
        using var stream = new MemoryStream();
        using (var document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation))
        {
            var presentation = document.AddPresentationPart();
            var first = presentation.AddNewPart<SlidePart>();
            first.Slide = Slide(
                TextShape(P.PlaceholderValues.Title, ("季度复盘", 0, false)),
                TextShape(null, ("收入增长 20%", 0, false), ("华东", 1, false), ("华南", 1, false)),
                new P.GraphicFrame(new A.Graphic(new A.GraphicData(new A.Table(
                    new A.TableRow(TableCell("区域"), TableCell("收入")),
                    new A.TableRow(TableCell("华东"), TableCell("120")))))));
            var notes = first.AddNewPart<NotesSlidePart>();
            notes.NotesSlide = new P.NotesSlide(new P.CommonSlideData(new P.ShapeTree(TextShape(null, ("重点讲华东的渠道变化", 0, false)))));
            var second = presentation.AddNewPart<SlidePart>();
            second.Slide = Slide(TextShape(null, ("谢谢", 0, false)));
            var hidden = presentation.AddNewPart<SlidePart>();
            hidden.Slide = Slide(TextShape(null, ("隐藏页", 0, false)));
            hidden.Slide.Show = false;
            presentation.Presentation = new P.Presentation(new P.SlideIdList(
                new P.SlideId { Id = 256, RelationshipId = presentation.GetIdOfPart(first) },
                new P.SlideId { Id = 257, RelationshipId = presentation.GetIdOfPart(second) },
                new P.SlideId { Id = 258, RelationshipId = presentation.GetIdOfPart(hidden) }));
        }

        stream.Position = 0;
        var result = new PresentationExtractor().Extract(stream, Ct);

        result.Content.Should().StartWith("## 季度复盘\n\n收入增长 20%\n\n  - 华东\n  - 华南\n");
        result.Content.Should().Contain("| 区域 | 收入 |\n| --- | --- |\n| 华东 | 120 |");
        result.Content.Should().Contain("备注：重点讲华东的渠道变化");
        result.Content.Should().Contain("## 第 2 页\n\n谢谢");
        result.Content.Should().NotContain("隐藏页");
        result.Pages.Should().Be(3);
    }

    [Fact]
    public void Pdf_drops_running_headers_and_page_numbers_and_detects_headings()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var number = 1; number <= 4; number++)
        {
            var page = builder.AddPage(PageSize.A4);
            page.AddText("ACME Internal Handbook", 9, new PdfPoint(50, 810), font);
            page.AddText($"Chapter {number}", 20, new PdfPoint(50, 740), font);
            page.AddText($"This chapter explains rule number {number} for every employee in the", 11, new PdfPoint(50, 700), font);
            page.AddText("company and applies from the first day of employment onwards.", 11, new PdfPoint(50, 686), font);
            page.AddText($"{number}", 9, new PdfPoint(297, 30), font);
        }

        using var stream = new MemoryStream(builder.Build());
        var result = new PdfExtractor().Extract(stream, Ct);

        result.Pages.Should().Be(4);
        result.Content.Should().Contain("# Chapter 1\n\nThis chapter explains rule number 1 for every employee in the company and applies from the first day of employment onwards.");
        result.Content.Should().Contain("# Chapter 4");
        result.Content.Should().NotContain("ACME Internal Handbook");
        result.Content.Split('\n').Should().NotContain(line => line.Trim() == "3");
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Pdf_without_a_text_layer_is_rejected_with_a_hint()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4);
        builder.AddPage(PageSize.A4);
        using var stream = new MemoryStream(builder.Build());

        FluentActions.Invoking(() => new PdfExtractor().Extract(stream, Ct))
            .Should().Throw<KnowledgeFileException>().Where(e => e.Code == "knowledge.file_no_text" && e.Message.Contains("OCR"));
    }

    [Fact]
    public void Broken_files_report_a_readable_error()
    {
        using var notZip = new MemoryStream(Encoding.UTF8.GetBytes("这不是 Word 文件"));
        FluentActions.Invoking(() => new WordExtractor().Extract(notZip, Ct)).Should().Throw<KnowledgeFileException>().Where(e => e.Code == "knowledge.file_unreadable");

        using var notPdf = new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.7 broken"));
        FluentActions.Invoking(() => new PdfExtractor().Extract(notPdf, Ct)).Should().Throw<KnowledgeFileException>();
    }

    [Fact]
    public void Html_keeps_structure_and_drops_scripts()
    {
        const string html = """
            <html><head><title>员工手册</title><style>p { color: red }</style></head>
            <body>
              <h1>入职</h1>
              <p>第一天请带好
                 身份证。</p>
              <script>alert('x')</script>
              <ul><li>领取电脑</li><li>开通邮箱 &amp; 账号</li></ul>
              <table><tr><th>部门</th><th>联系人</th></tr><tr><td>IT</td><td>王工</td></tr></table>
              <pre>  缩进保留
            第二行</pre>
            </body></html>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));
        var result = new HtmlExtractor().Extract(stream, Ct);
        var content = KnowledgeFiles.Normalize(result.Content);

        result.Title.Should().Be("员工手册");
        content.Should().Be("# 入职\n\n第一天请带好 身份证。\n\n- 领取电脑\n- 开通邮箱 & 账号\n\n| 部门 | 联系人 |\n| --- | --- |\n| IT | 王工 |\n\n  缩进保留\n第二行");
    }

    [Fact]
    public void Text_files_detect_gbk_and_bom()
    {
        var gbk = CodePagesEncoding("GB18030").GetBytes("名称,备注\n鼠标,无线");
        using var gbkStream = new MemoryStream(gbk);
        new PlainTextExtractor().Extract(gbkStream, Ct).Content.Should().Be("名称,备注\n鼠标,无线");

        using var utf16 = new MemoryStream([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("你好")]);
        new PlainTextExtractor().Extract(utf16, Ct).Content.Should().Be("你好");

        using var utf8 = new MemoryStream([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("# 标题")]);
        new PlainTextExtractor().Extract(utf8, Ct).Content.Should().Be("# 标题");
    }

    private static Encoding CodePagesEncoding(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(name);
    }

    private static W.Style ParagraphStyle(string id, string name)
        => new(new W.StyleName { Val = name }) { Type = W.StyleValues.Paragraph, StyleId = id };

    private static W.Paragraph Paragraph(string text, string? style = null)
    {
        var paragraph = new W.Paragraph(new W.Run(new W.Text(text)));
        if (style is not null)
        {
            paragraph.PrependChild(new W.ParagraphProperties(new W.ParagraphStyleId { Val = style }));
        }

        return paragraph;
    }

    private static W.Paragraph ListItem(string text)
        => new(new W.ParagraphProperties(new W.NumberingProperties(new W.NumberingLevelReference { Val = 0 }, new W.NumberingId { Val = 1 })), new W.Run(new W.Text(text)));

    private static W.TableRow Row(params string[] cells)
        => new(cells.Select(c => new W.TableCell(new W.Paragraph(new W.Run(new W.Text(c))))));

    private static S.Cell Shared(string reference, int index)
        => new() { CellReference = reference, DataType = S.CellValues.SharedString, CellValue = new S.CellValue(index.ToString(System.Globalization.CultureInfo.InvariantCulture)) };

    private static P.Slide Slide(params OpenXmlElement[] shapes) => new(new P.CommonSlideData(new P.ShapeTree(shapes)));

    private static P.Shape TextShape(P.PlaceholderValues? placeholder, params (string Text, int Level, bool Bullet)[] paragraphs)
    {
        var properties = new P.ApplicationNonVisualDrawingProperties();
        if (placeholder is { } type)
        {
            properties.PlaceholderShape = new P.PlaceholderShape { Type = type };
        }

        return new P.Shape(
            new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Id = 1, Name = "文本" }, new P.NonVisualShapeDrawingProperties(), properties),
            new P.ShapeProperties(),
            new P.TextBody([new A.BodyProperties(), .. paragraphs.Select(p => new A.Paragraph(new A.ParagraphProperties { Level = p.Level }, new A.Run(new A.Text(p.Text))))]));
    }

    private static A.TableCell TableCell(string text) => new(new A.TextBody(new A.BodyProperties(), new A.Paragraph(new A.Run(new A.Text(text)))));
}

using System.Text;
using AiNative.Application.Knowledge;
using AiNative.Infrastructure.Files;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace AiNative.Infrastructure.Knowledge.Files;

/// <summary>
/// PowerPoint（.pptx）：每页一个「## 标题」（没有标题时写第几页），下面是文本框里的段落（有缩进级别的转成列表）、
/// 表格和演讲者备注。隐藏的页跳过。
/// </summary>
internal sealed class PresentationExtractor : IKnowledgeFileExtractor
{
    public string Format => "PowerPoint";

    public IReadOnlyCollection<string> Extensions { get; } = [".pptx", ".pptm"];

    public ExtractedText Extract(Stream content, CancellationToken cancellationToken)
    {
        using var document = KnowledgeOpenXml.Open(() => PresentationDocument.Open(content, false, OpenXmlFiles.Settings), " PowerPoint ");
        var presentation = document.PresentationPart;
        var slideIds = presentation?.Presentation?.SlideIdList?.Elements<P.SlideId>().ToList();
        if (presentation is null || slideIds is null || slideIds.Count == 0)
        {
            throw new KnowledgeFileException("knowledge.file_unreadable", "这个 PowerPoint 文件里没有幻灯片。");
        }

        var builder = new StringBuilder();
        var number = 0;
        foreach (var slideId in slideIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            number++;
            if (slideId.RelationshipId?.Value is not { } relationshipId
                || presentation.GetPartById(relationshipId) is not SlidePart part
                || part.Slide is not { } slide
                || slide.Show?.Value == false)
            {
                continue;
            }

            var shapes = slide.CommonSlideData?.ShapeTree?.Descendants<P.Shape>().ToList() ?? [];
            var titleShape = shapes.FirstOrDefault(IsTitle);
            var title = titleShape is null ? string.Empty : string.Join(" ", Paragraphs(titleShape.TextBody).Select(p => p.Text)).Trim();
            builder.Append("## ").Append(title.Length > 0 ? title : $"第 {number} 页").Append("\n\n");

            foreach (var element in slide.CommonSlideData?.ShapeTree?.Descendants() ?? [])
            {
                switch (element)
                {
                    case P.Shape shape when shape != titleShape:
                        AppendParagraphs(builder, Paragraphs(shape.TextBody));
                        break;
                    case A.Table table:
                        MarkdownTable.Append(builder, [.. table.Elements<A.TableRow>().Select(row => (IReadOnlyList<string>)[.. row.Elements<A.TableCell>().Select(c => string.Join(" ", Paragraphs(c.TextBody).Select(p => p.Text)))])]);
                        break;
                }
            }

            var notes = part.NotesSlidePart?.NotesSlide?.CommonSlideData?.ShapeTree?.Descendants<P.Shape>()
                .Where(s => PlaceholderType(s) is not { } type || !NotesChrome.Contains(type))
                .SelectMany(s => Paragraphs(s.TextBody))
                .Select(p => p.Text.Trim())
                .Where(t => t.Length > 0)
                .ToList();
            if (notes is { Count: > 0 })
            {
                builder.Append("备注：").Append(string.Join("\n", notes)).Append("\n\n");
            }
        }

        return new ExtractedText(builder.ToString(), OpenXmlFiles.Title(document), slideIds.Count);
    }

    private static void AppendParagraphs(StringBuilder builder, IEnumerable<(string Text, int Level, bool Bulleted)> paragraphs)
    {
        var any = false;
        foreach (var (text, level, bulleted) in paragraphs)
        {
            var line = text.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (bulleted || level > 0)
            {
                builder.Append(new string(' ', Math.Min(level, 4) * 2)).Append("- ").Append(line).Append('\n');
            }
            else
            {
                builder.Append(line).Append("\n\n");
            }

            any = true;
        }

        if (any)
        {
            builder.Append('\n');
        }
    }

    private static IEnumerable<(string Text, int Level, bool Bulleted)> Paragraphs(OpenXmlCompositeElement? textBody)
    {
        if (textBody is null)
        {
            yield break;
        }

        foreach (var paragraph in textBody.Elements<A.Paragraph>())
        {
            var builder = new StringBuilder();
            foreach (var node in paragraph.Descendants())
            {
                switch (node)
                {
                    case A.Text text:
                        builder.Append(text.Text);
                        break;
                    case A.Break:
                        builder.Append(' ');
                        break;
                }
            }

            var properties = paragraph.ParagraphProperties;
            var bulleted = properties?.GetFirstChild<A.CharacterBullet>() is not null || properties?.GetFirstChild<A.AutoNumberedBullet>() is not null;
            yield return (builder.ToString(), properties?.Level?.Value ?? 0, bulleted);
        }
    }

    // 备注页上的缩略图、页码、页眉页脚不是备注内容。
    private static readonly HashSet<P.PlaceholderValues> NotesChrome =
        [P.PlaceholderValues.SlideImage, P.PlaceholderValues.SlideNumber, P.PlaceholderValues.Header, P.PlaceholderValues.Footer, P.PlaceholderValues.DateAndTime];

    private static bool IsTitle(P.Shape shape) => PlaceholderType(shape) is { } type && (type == P.PlaceholderValues.Title || type == P.PlaceholderValues.CenteredTitle);

    private static P.PlaceholderValues? PlaceholderType(P.Shape shape)
        => shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape?.Type?.Value;
}

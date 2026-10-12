using System.IO.Packaging;
using AiNative.Application.Knowledge;
using DocumentFormat.OpenXml.Packaging;

namespace AiNative.Infrastructure.Knowledge.Files;

/// <summary>
/// Office Open XML（docx / xlsx / pptx）共用的打开方式：只读，限制单个部件的大小，防止压缩包解开后撑爆内存。
/// </summary>
internal static class OpenXmlFiles
{
    public static readonly OpenSettings Settings = new()
    {
        AutoSave = false,
        // 单个部件最多 3000 万字符（约 60 MB 内存），足够容纳正常文档，挡住解压炸弹。
        MaxCharactersInPart = 30_000_000,
    };

    public static T Open<T>(Func<T> open, string format)
        where T : OpenXmlPackage
    {
        try
        {
            return open();
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or FileFormatException or InvalidDataException or IOException)
        {
            throw new KnowledgeFileException("knowledge.file_unreadable", $"无法打开这个{format}文件，可能已损坏、加了密码，或者扩展名和实际格式不符。");
        }
    }

    public static string? Title(OpenXmlPackage package)
    {
        var title = package.PackageProperties.Title;
        return string.IsNullOrWhiteSpace(title) ? null : title.Trim();
    }
}

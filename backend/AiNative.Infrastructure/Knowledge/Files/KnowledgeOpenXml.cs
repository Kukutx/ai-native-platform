using AiNative.Application.Knowledge;
using AiNative.Infrastructure.Files;
using DocumentFormat.OpenXml.Packaging;

namespace AiNative.Infrastructure.Knowledge.Files;

internal static class KnowledgeOpenXml
{
    public static T Open<T>(Func<T> open, string format)
        where T : OpenXmlPackage
        => OpenXmlFiles.Open(open, () => new KnowledgeFileException("knowledge.file_unreadable", $"无法打开这个{format}文件，可能已损坏、加了密码，或者扩展名和实际格式不符。"));
}

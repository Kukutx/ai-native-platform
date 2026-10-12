using AiNative.Application.Common;

namespace AiNative.Application.Files;

/// <summary>
/// 表格文件（CSV、TSV、Excel）的内容。第一行有内容的行作表头；完全空白的行跳过。
/// </summary>
/// <param name="Format">给用户看的格式名，例如 CSV、Excel。</param>
public sealed record TabularData(string Format, IReadOnlyList<string> Headers, IReadOnlyList<TabularRow> Rows);

/// <param name="Number">在文件里的行号（从 1 开始，表头也算），用于提示哪一行出错。</param>
public sealed record TabularRow(int Number, IReadOnlyList<string> Cells)
{
    public string Cell(int index) => index < Cells.Count ? Cells[index] : string.Empty;
}

/// <param name="MaxRows">数据行（不含表头）上限，超过时报错。</param>
/// <param name="MaxColumns">列数上限，超过时报错。</param>
public sealed record TabularLimits(int MaxRows, int MaxColumns);

/// <summary>
/// 读取表格文件。实现依赖解析库，放在基础设施层。
/// </summary>
public interface ITabularFileReader
{
    /// <summary>支持的扩展名（小写，带点）。</summary>
    IReadOnlyCollection<string> Extensions { get; }

    /// <param name="content">可定位的流（已整体读入内存）。</param>
    /// <exception cref="TabularFileException">格式不支持、文件损坏、没有内容或超过上限。</exception>
    TabularData Read(string fileName, Stream content, TabularLimits limits, CancellationToken cancellationToken);
}

/// <summary>
/// 表格文件读不了或不符合要求。消息直接展示给用户。
/// </summary>
public sealed class TabularFileException(string code, string message) : AppException(code, message);

/// <summary>
/// 上传文件的公共处理：限制大小并整体读进内存（解析库需要可定位的流）。
/// </summary>
public static class UploadedFiles
{
    /// <param name="length">客户端声明的大小，用于预先拒绝和分配缓冲区；实际读取同样受上限约束。</param>
    /// <param name="tooLarge">超过上限时抛出的异常。</param>
    public static async Task<MemoryStream> ReadAsync(Stream source, long length, long maxBytes, Func<Exception> tooLarge, CancellationToken cancellationToken)
    {
        if (length > maxBytes)
        {
            throw tooLarge();
        }

        var buffer = new MemoryStream((int)Math.Clamp(length, 0, maxBytes));
        try
        {
            var chunk = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw tooLarge();
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            buffer.Position = 0;
            return buffer;
        }
        catch
        {
            await buffer.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 只保留文件名部分，过长时保留结尾（扩展名）。
    /// </summary>
    public static string SafeName(string fileName)
    {
        var name = Path.GetFileName(fileName.Trim());
        return name.Length > 260 ? name[^260..] : name;
    }
}

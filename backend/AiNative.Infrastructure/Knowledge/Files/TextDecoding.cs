using System.Text;

namespace AiNative.Infrastructure.Knowledge.Files;

/// <summary>
/// 文本文件的编码识别：有 BOM 按 BOM，否则先按 UTF-8 严格解码，失败再按 GB18030（兼容 GBK / GB2312）。
/// 国内常见的记事本、Excel 导出的 CSV 多是 GBK。
/// </summary>
internal static class TextDecoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static TextDecoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string Decode(Stream content)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        return Decode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
    }
}

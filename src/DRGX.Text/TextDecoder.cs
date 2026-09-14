using System.Text;

namespace DRGX.Text;

/// <summary>
/// 文本解码:UTF-16 BOM(LE/BE) → UTF-8 BOM → 严格 UTF-8 → GB18030 → GBK → UTF-8 兜底。
/// HQMS 等医疗导出常为 GBK/GB18030,故保留中文回退链。纯函数、无 IO。
/// </summary>
public static class TextDecoder
{
    static TextDecoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string Decode(byte[] bytes)
    {
        // UTF-16 BOM:Excel「另存为 CSV」偶发导出 UTF-16 LE(FF FE) / BE(FE FF)
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Utf8Strict.GetString(bytes, 3, bytes.Length - 3);
        try { return Utf8Strict.GetString(bytes); }
        catch (DecoderFallbackException) { /* 非 UTF-8,回落 GB18030/GBK */ }
        try { return Encoding.GetEncoding("gb18030").GetString(bytes); }
        catch { /* 编码不可用,继续回落 */ }
        try { return Encoding.GetEncoding("gbk").GetString(bytes); }
        catch { /* 编码不可用,继续回落 */ }
        return Encoding.UTF8.GetString(bytes);
    }

    private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}

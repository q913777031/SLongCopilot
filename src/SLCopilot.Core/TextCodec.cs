using System.Text;

namespace SLCopilot.Core;

/// <summary>按 BOM、严格 UTF-8、回退代码页的顺序识别出的文本编码；写回时使用同一编码与 BOM，保证不改变文件编码。</summary>
public sealed class TextFormat
{
    internal TextFormat(Encoding encoding, byte[] preamble, string name)
    {
        Encoding = encoding;
        Preamble = preamble;
        Name = name;
    }

    /// <summary>严格编码：遇到无法表示的字符抛出异常，不做替换。</summary>
    public Encoding Encoding { get; }
    public byte[] Preamble { get; }
    /// <summary>告知模型与用户的编码名称，例如 utf-8、utf-8-bom、utf-16le、gb2312（代码页 936）。</summary>
    public string Name { get; }
}

/// <summary>
/// 文本文件编解码。识别顺序与 Visual Studio 打开文件一致：BOM（UTF-8 / UTF-16）→ 无 BOM 的严格 UTF-8 → 回退代码页
/// （由调用方提供，VS 扩展中为 devenv 进程的系统 ANSI 代码页，如中文 Windows 的 GBK/936）。
/// 无法解码或含 NUL 字符时抛出 <see cref="WorkspaceException"/>；写回时编码失败同样抛出，不做有损替换。
/// </summary>
public sealed class TextCodec
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LeBom = [0xFF, 0xFE];
    private static readonly byte[] Utf16BeBom = [0xFE, 0xFF];
    private static readonly TextFormat Utf8 = new(new UTF8Encoding(false, true), [], "utf-8");
    private static readonly TextFormat Utf8WithBom = new(new UTF8Encoding(false, true), Utf8Bom, "utf-8-bom");
    private static readonly TextFormat Utf16Le = new(new UnicodeEncoding(false, false, true), Utf16LeBom, "utf-16le");
    private static readonly TextFormat Utf16Be = new(new UnicodeEncoding(true, false, true), Utf16BeBom, "utf-16be");
    private readonly TextFormat? fallback;

    /// <param name="fallbackCodePage">无 BOM 且不是合法 UTF-8 时使用的代码页；为 null、65001 或 UTF-16 代码页时不回退。</param>
    public TextCodec(int? fallbackCodePage)
    {
        // UTF-8 / UTF-16 已在前面识别；UTF-7（65000）是有状态编码，不作为回退。
        if (fallbackCodePage is not { } codePage || codePage is 65000 or 65001 or 1200 or 1201) return;
        var encoding = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        fallback = new(encoding, [], encoding.WebName + " (代码页 " + codePage + ")");
    }

    /// <summary>新建文件使用的编码：无 BOM 的 UTF-8。</summary>
    public static TextFormat NewFile => Utf8;

    public (string Text, TextFormat Format) Decode(byte[] bytes)
    {
        if (StartsWith(bytes, Utf8Bom)) return (Strict(Utf8WithBom, bytes), Utf8WithBom);
        if (StartsWith(bytes, Utf16LeBom)) return (Strict(Utf16Le, bytes), Utf16Le);
        if (StartsWith(bytes, Utf16BeBom)) return (Strict(Utf16Be, bytes), Utf16Be);
        try { return (Strict(Utf8, bytes), Utf8); }
        catch (WorkspaceException) when (fallback is not null) { return (Strict(fallback, bytes), fallback); }
    }

    /// <summary>按原文件编码（含 BOM）编码文本；字符无法用该编码表示时拒绝，提示模型保持原编码可表示的字符。</summary>
    public static byte[] Encode(string text, TextFormat format)
    {
        if (text.IndexOf('\0') >= 0) throw new WorkspaceException("Output must not contain NUL characters.");
        try
        {
            var body = format.Encoding.GetBytes(text);
            if (format.Preamble.Length == 0) return body;
            var output = new byte[format.Preamble.Length + body.Length];
            Buffer.BlockCopy(format.Preamble, 0, output, 0, format.Preamble.Length);
            Buffer.BlockCopy(body, 0, output, format.Preamble.Length, body.Length);
            return output;
        }
        catch (EncoderFallbackException)
        {
            throw new WorkspaceException($"The content contains characters that cannot be saved in the file's encoding ({format.Name}). Keep the file's encoding; avoid those characters.");
        }
    }

    private static string Strict(TextFormat format, byte[] bytes)
    {
        string text;
        try { text = format.Encoding.GetString(bytes, format.Preamble.Length, bytes.Length - format.Preamble.Length); }
        catch (DecoderFallbackException) { throw new WorkspaceException("Binary files or files in an unsupported encoding cannot be read."); }
        if (text.IndexOf('\0') >= 0) throw new WorkspaceException("Binary files are not supported.");
        return text;
    }

    private static bool StartsWith(byte[] bytes, byte[] prefix)
    {
        if (bytes.Length < prefix.Length) return false;
        for (var i = 0; i < prefix.Length; i++) if (bytes[i] != prefix[i]) return false;
        return true;
    }
}

using System.Text;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class TextCodecTests
{
    static TextCodecTests()
    {
#if NET
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif
        Gbk = new(936);
    }

    private static readonly TextCodec Gbk;

    [Fact]
    public void Utf8WithoutBomIsPreferredOverTheFallbackCodePage()
    {
        var bytes = new UTF8Encoding(false).GetBytes("// 中文注释");
        var (text, format) = Gbk.Decode(bytes);
        Assert.Equal("// 中文注释", text);
        Assert.Equal("utf-8", format.Name);
        Assert.Equal(bytes, TextCodec.Encode(text, format));
    }

    [Fact]
    public void GbkFilesAreDecodedAndWrittenBackInGbk()
    {
        var gbk = Encoding.GetEncoding(936);
        var bytes = gbk.GetBytes("// 中文注释\r\nint a;");
        var (text, format) = Gbk.Decode(bytes);
        Assert.Equal("// 中文注释\r\nint a;", text);
        Assert.Contains("936", format.Name);
        Assert.Equal(gbk.GetBytes("// 修改后\r\nint a;"), TextCodec.Encode("// 修改后\r\nint a;", format));
    }

    [Fact]
    public void CharactersTheOriginalEncodingCannotRepresentAreRejected()
    {
        var (_, format) = Gbk.Decode(Encoding.GetEncoding(936).GetBytes("// 中文注释"));
        Assert.Throws<WorkspaceException>(() => TextCodec.Encode("// 😀", format));
    }

    [Fact]
    public void BomsArePreservedOnWrite()
    {
        foreach (var encoding in new Encoding[] { new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true) })
        {
            var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("类 A")).ToArray();
            var (text, format) = Gbk.Decode(bytes);
            Assert.Equal("类 A", text);
            Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes("类 B")).ToArray(), TextCodec.Encode("类 B", format));
        }
    }

    [Fact]
    public void WithoutFallbackNonUtf8AndBinaryFilesAreRejected()
    {
        var codec = new TextCodec(null);
        Assert.Throws<WorkspaceException>(() => codec.Decode(Encoding.GetEncoding(936).GetBytes("中文")));
        Assert.Throws<WorkspaceException>(() => Gbk.Decode([0x41, 0x00, 0x42]));
    }
}

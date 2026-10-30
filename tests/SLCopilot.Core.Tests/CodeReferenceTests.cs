using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

/// <summary>回答中可点击的文件与符号引用的识别。</summary>
public sealed class CodeReferenceTests
{
    [Theory]
    [InlineData("src/Foo.cs", "src/Foo.cs", 0)]
    [InlineData("Foo.cs:42", "Foo.cs", 42)]
    [InlineData("src\\Bar.xaml:7-12", "src\\Bar.xaml", 7)]
    [InlineData("Foo.cs(15)", "Foo.cs", 15)]
    [InlineData("Foo.cs#L9", "Foo.cs", 9)]
    [InlineData("C:\\work\\App\\Main.cs:3", "C:\\work\\App\\Main.cs", 3)]
    public void ParsesFileReferences(string text, string path, int line)
    {
        Assert.True(CodeReference.TryParseFile(text, out var parsedPath, out var parsedLine));
        Assert.Equal(path, parsedPath);
        Assert.Equal(line, parsedLine);
    }

    [Theory]
    [InlineData("Foo")]
    [InlineData("hello world.cs")]
    [InlineData("1.5")]
    [InlineData("Foo.exe")]
    [InlineData("\\\\attacker.example\\s\\a.cs")]
    [InlineData("//attacker.example/s/a.cs:3")]
    public void RejectsNonFiles(string text) => Assert.False(CodeReference.TryParseFile(text, out _, out _));

    [Theory]
    [InlineData("OrderService.Save", true)]
    [InlineData("OrderService.Save(int)", true)]
    [InlineData("OrderService", true)]
    [InlineData("List<T>.Add", true)]
    [InlineData("null", false)]
    [InlineData("Task", false)]
    [InlineData("Foo.cs", false)]
    [InlineData("a + b", false)]
    public void RecognizesSymbols(string text, bool expected) => Assert.Equal(expected, CodeReference.TryParseSymbol(text, out _));

    [Fact]
    public void FindsLineReferencesInProse()
    {
        var matches = CodeReference.InlineFilePattern.Matches("改动见 src/App/Foo.cs:42 与 Bar.xaml(7)，不含 Foo.cs 本身。");
        Assert.Equal(["src/App/Foo.cs:42", "Bar.xaml(7)"], matches.Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value));
    }
}

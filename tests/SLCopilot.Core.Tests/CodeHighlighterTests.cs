using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class CodeHighlighterTests
{
    private static string[] Of(string code, string language, CodeTokenKind kind) =>
        CodeHighlighter.Tokenize(code, language).Where(t => t.Kind == kind).Select(t => code.Substring(t.Start, t.Length)).ToArray();

    [Fact]
    public void CSharpKeywordsStringsCommentsAndPreprocessorAreFound()
    {
        const string code = "#region A\npublic async Task<int> Run(string s) // 注释\n{\n    var x = @\"a\"\"b\" + $\"{s}\\\"\";\n    return 42; /* end */\n}";
        Assert.Equal(["#region A"], Of(code, "csharp", CodeTokenKind.Preprocessor));
        Assert.Contains("public", Of(code, "cs", CodeTokenKind.Keyword));
        Assert.Contains("return", Of(code, "cs", CodeTokenKind.Keyword));
        Assert.Equal(["Task", "int", "string"], Of(code, "cs", CodeTokenKind.Type));
        Assert.Equal(["@\"a\"\"b\"", "$\"{s}\\\"\""], Of(code, "cs", CodeTokenKind.String));
        Assert.Equal(["// 注释", "/* end */"], Of(code, "cs", CodeTokenKind.Comment));
        Assert.Equal(["42"], Of(code, "cs", CodeTokenKind.Number));
    }

    [Fact]
    public void TokensNeverOverlapAndStayInsideTheText()
    {
        foreach (var (code, language) in new[]
        {
            ("<Grid x:Name=\"Root\"><!-- c --><TextBlock Text='hi'/></Grid>", "xaml"),
            ("{\"a\": [1, -2.5e3, true, null], \"b\": \"x\"}", "json"),
            ("def f(x):\n    '''doc'''\n    return x # c", "python"),
            ("SELECT * FROM t -- c\nWHERE id = 1", "sql"),
            ("const s = `a\nb`; // c", "ts"),
            ("\"unterminated", "cs"),
        })
        {
            var tokens = CodeHighlighter.Tokenize(code, language);
            Assert.NotEmpty(tokens);
            for (var i = 0; i < tokens.Count; i++)
            {
                Assert.True(tokens[i].Length > 0 && tokens[i].Start + tokens[i].Length <= code.Length, $"{language}: {tokens[i]}");
                if (i > 0) Assert.True(tokens[i].Start >= tokens[i - 1].Start + tokens[i - 1].Length, $"{language}: overlap at {tokens[i]}");
            }
        }
    }

    [Fact]
    public void XmlAndJsonDistinguishNamesFromValues()
    {
        Assert.Equal(["x:Name"], Of("<Grid x:Name=\"Root\"/>", "xml", CodeTokenKind.Attribute));
        Assert.Equal(["\"Root\""], Of("<Grid x:Name=\"Root\"/>", "xml", CodeTokenKind.String));
        Assert.Equal(["\"a\""], Of("{\"a\": \"b\"}", "json", CodeTokenKind.Attribute));
        Assert.Equal(["\"b\""], Of("{\"a\": \"b\"}", "json", CodeTokenKind.String));
        Assert.Equal(["select", "FROM"], Of("select a FROM t", "sql", CodeTokenKind.Keyword));
    }

    [Fact]
    public void UnknownLanguagesAndHugeCodeAreNotHighlighted()
    {
        Assert.Empty(CodeHighlighter.Tokenize("int x;", "brainfuck"));
        Assert.Empty(CodeHighlighter.Tokenize("int x;", ""));
        Assert.Empty(CodeHighlighter.Tokenize(new string('a', CodeHighlighter.MaxChars + 1), "cs"));
    }
}

using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class MarkdownTests
{
    [Fact]
    public void ParsesCommonChatStructures()
    {
        var blocks = Markdown.Parse("""
            # 标题
            Some **bold** and `code` text.

            - first
              continued
            - second
            1. one
            > quoted

            ```csharp
            var x = 1;

            var y = 2;
            ```
            ---
            """);
        Assert.Equal([MdBlockKind.Heading1, MdBlockKind.Paragraph, MdBlockKind.Bullet, MdBlockKind.Bullet, MdBlockKind.Numbered,
            MdBlockKind.Quote, MdBlockKind.Code, MdBlockKind.Rule], blocks.Select(b => b.Kind));
        Assert.Equal("标题", blocks[0].Text);
        Assert.True(blocks[1].HasFormatting);
        Assert.Equal("Some bold and code text.", blocks[1].Text);
        Assert.Equal("first continued", blocks[2].Text);
        Assert.Equal("•", blocks[2].Marker);
        Assert.Equal("1.", blocks[4].Marker);
        Assert.Equal("csharp", blocks[6].Language);
        Assert.Equal("var x = 1;\n\nvar y = 2;", blocks[6].Text);
    }

    [Fact]
    public void UnterminatedFenceKeepsRestAsCode()
    {
        var blocks = Markdown.Parse("text\n```\n# not a heading\n- not a list");
        Assert.Equal(MdBlockKind.Code, blocks[^1].Kind);
        Assert.Equal("# not a heading\n- not a list", blocks[^1].Text);
    }

    [Fact]
    public void InlineParsingHandlesLinksItalicsAndSnakeCase()
    {
        var inlines = Markdown.ParseInlines("see [docs](https://x.y) for *this* and my_var_name, 2*3*4");
        Assert.Contains(new MdInline(MdInlineKind.Link, "docs", "https://x.y"), inlines);
        Assert.Contains(new MdInline(MdInlineKind.Italic, "this"), inlines);
        Assert.Contains(inlines, i => i.Kind == MdInlineKind.Text && i.Text.Contains("my_var_name"));
    }

    [Fact]
    public void TableRowsKeepInlineFormattingAndCodeWithPipes()
    {
        var block = Markdown.Parse("| 项 | 文件 |\n|---|:--:|\n| **改名** | `A.cs:12` |\n| 管道 | `a | b` 与 x\\|y |").Single();
        var rows = block.Rows!;
        Assert.Equal(3, rows.Count);
        Assert.Equal(MdInlineKind.Bold, rows[1][0][0].Kind);
        Assert.Equal(MdInlineKind.Code, rows[1][1][0].Kind);
        Assert.Equal("A.cs:12", rows[1][1][0].Text);
        Assert.Equal(2, rows[2].Count);
        Assert.Equal("a | b", rows[2][1][0].Text);
        Assert.Contains("x|y", string.Concat(rows[2][1].Select(i => i.Text)));
    }

    [Fact]
    public void TablesAreAligned()
    {
        var block = Assert.Single(Markdown.Parse("| A | 名称 |\n|---|---|\n| 1 | x |"));
        Assert.Equal(MdBlockKind.Table, block.Kind);
        Assert.Equal(3, block.Text.Split('\n').Length);
        Assert.StartsWith("A  名称", block.Text);
    }

    [Fact]
    public void TokenizerSplitsWordsAndCjkCharacters()
    {
        var tokens = Markdown.Tokenize([new(MdInlineKind.Text, "Hello world 你好"), new(MdInlineKind.Code, "a b")])!;
        Assert.Equal(["Hello ", "world ", "你", "好", "a b"], tokens.Select(t => t.Text));
        Assert.Null(Markdown.Tokenize([new(MdInlineKind.Text, new string('字', 2000))]));
    }

    [Fact]
    public void PlainTextHasNoFormatting()
        => Assert.False(Assert.Single(Markdown.Parse("just text\nsecond line")).HasFormatting);
}

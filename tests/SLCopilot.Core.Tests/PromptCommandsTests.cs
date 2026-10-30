using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class PromptCommandsTests
{
    [Fact]
    public void SlashCommandsExpandAndChooseTheirMode()
    {
        var fix = PromptCommands.Parse("/fix 空引用异常");
        Assert.Equal("fix", fix.Command!.Name);
        Assert.Equal(RunMode.Agent, fix.Command.Mode);
        Assert.StartsWith("请找出并修复问题", fix.Text);
        Assert.EndsWith("空引用异常", fix.Text);
        Assert.Equal(RunMode.Ask, PromptCommands.Parse("/EXPLAIN").Command!.Mode);
        Assert.Equal("clear", PromptCommands.Parse("/clear").Command!.Name);
    }

    [Fact]
    public void UnknownCommandsAndSlashesInsideTextStayPlainText()
    {
        var parsed = PromptCommands.Parse("/unknown 看看 a/b");
        Assert.Null(parsed.Command);
        Assert.Equal("/unknown 看看 a/b", parsed.Text);
    }

    [Fact]
    public void FileReferencesAreCollectedOnceAndQuotedPathsSupported()
    {
        var parsed = PromptCommands.Parse("对比 #file:src/A.cs 和 #file:\"docs/My File.md\"，再看 #file:src\\A.cs");
        Assert.Equal(["src/A.cs", "docs/My File.md"], parsed.Files);
        Assert.Contains("`src/A.cs`", parsed.Text);
        Assert.Contains("read_file", PromptCommands.ReferenceNote(parsed.Files));
        Assert.Equal("", PromptCommands.ReferenceNote([]));
    }
}

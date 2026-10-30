using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

/// <summary>输入框 @ 补全使用的文件列表与匹配排序。</summary>
public sealed class FileIndexTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SLCopilot-file-index-" + Guid.NewGuid().ToString("N"));

    public FileIndexTests()
    {
        foreach (var file in new[] { "src/App/OrderService.cs", "src/App/Orders/OrderView.xaml", "src/App/bin/Debug/App.dll", "src/App/obj/x.cs", ".git/config", "README.md", "logo.png" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, file))!);
            System.IO.File.WriteAllText(Path.Combine(root, file), "");
        }
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    [Fact]
    public void BuildSkipsHiddenGeneratedAndBinaryFiles()
        => Assert.Equal(["README.md", "src/App/Orders/OrderView.xaml", "src/App/OrderService.cs"], FileIndex.Build(root));

    [Fact]
    public void MatchRanksNamePrefixFirstThenContainsThenFuzzy()
    {
        var files = new[] { "src/App/OrderService.cs", "src/App/Orders/OrderView.xaml", "src/App/MyOrderHelper.cs", "docs/order-notes.md", "src/OtherService.cs" };
        Assert.Equal(["docs/order-notes.md", "src/App/OrderService.cs", "src/App/Orders/OrderView.xaml", "src/App/MyOrderHelper.cs"], FileIndex.Match(files, "order"));
        Assert.Equal(["src/App/OrderService.cs"], FileIndex.Match(files, "ordsvc"));
        Assert.Empty(FileIndex.Match(files, " "));
    }

    [Fact]
    public void AtPathReferencesAreParsedLikeHashFile()
    {
        var parsed = PromptCommands.Parse("看看 @src/App/OrderService.cs 和 #file:README.md，联系 a@b.com 或 @张三");
        Assert.Equal(["src/App/OrderService.cs", "README.md"], parsed.Files);
        Assert.Contains("`src/App/OrderService.cs`", parsed.Text);
        Assert.Contains("a@b.com", parsed.Text);
        Assert.Contains("@张三", parsed.Text);
    }

    [Theory]
    [InlineData("修改 @Model.Name 的显示")]
    [InlineData("@app.route 的写法")]
    [InlineData("用 @Html.Raw(x) 输出")]
    public void AtCodeIsNotAFileReference(string prompt) => Assert.Empty(PromptCommands.Parse(prompt).Files);

    [Fact]
    public void AtFileWithKnownExtensionIsAReference() => Assert.Equal(["docs/readme.md"], PromptCommands.Parse("看 @docs/readme.md").Files);
}

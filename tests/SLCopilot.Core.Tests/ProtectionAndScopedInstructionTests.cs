using System.Text.Json;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

/// <summary>0.7.1：写入保护规则、按路径生效的指令、用户级指令。</summary>
public sealed class ProtectionAndScopedInstructionTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "SLCopilot-protect-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(temp, "project");
    public ProtectionAndScopedInstructionTests() => Directory.CreateDirectory(Root);
    public void Dispose() { try { Directory.Delete(temp, true); } catch (IOException) { } }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text);
    }

    private WorkspaceTools Workspace() => new(Root, true, Path.Combine(temp, "backups"));
    private static Task<ToolResult> Call(WorkspaceTools tools, string name, object args)
        => tools.ExecuteAsync(name, JsonSerializer.SerializeToElement(args), CancellationToken.None);

    [Fact]
    public void ProtectionPatternsMatchNamesFoldersAndPaths()
    {
        Write(".slcopilot/settings.json", """{ "protect": ["Recipes/", "*.recipe.json", "src/Config/**"] } // comment""");
        var protection = WriteProtection.Load(Root);
        Assert.False(protection.ProtectsAll, protection.LoadError);
        Assert.NotNull(protection.Check("Recipes/a.json"));
        Assert.NotNull(protection.Check("deep/x.recipe.json"));
        Assert.NotNull(protection.Check("src/Config/App/settings.json"));
        Assert.NotNull(protection.Check(".slcopilot/settings.json"));
        Assert.Null(protection.Check("src/App/Recipes.cs"));
        Assert.Null(protection.Check("src/ConfigLoader.cs"));
    }

    [Fact]
    public void UnreadableSettingsProtectEverything()
    {
        Write(".slcopilot/settings.json", "{ \"protect\": \"Recipes\" }");
        var protection = WriteProtection.Load(Root);
        Assert.True(protection.ProtectsAll);
        Assert.Contains("could not be read", protection.Check("src/A.cs"));
        Assert.Null(WriteProtection.Load(Path.Combine(temp, "missing")).Check("a.cs"));
    }

    [Fact]
    public async Task ProtectedPathsCannotBeWrittenDeletedOrMovedInto()
    {
        Write(".slcopilot/settings.json", """{ "protect": ["Recipes/"] }""");
        Write("Recipes/r.json", "{}");
        Write("A.cs", "class A { }");
        var tools = Workspace();
        var read = await Call(tools, "read_file", new { path = "Recipes/r.json" });
        Assert.False(read.IsError, read.Content);
        var hash = JsonDocument.Parse(read.Content).RootElement.GetProperty("sha256").GetString();
        Assert.Contains("write-protected", (await Call(tools, "edit_file", new { path = "Recipes/r.json", old_text = "{}", new_text = "{ }" })).Content);
        Assert.Contains("write-protected", (await Call(tools, "delete_file", new { path = "Recipes/r.json", expected_sha256 = hash })).Content);
        var a = JsonDocument.Parse((await Call(tools, "read_file", new { path = "A.cs" })).Content).RootElement.GetProperty("sha256").GetString();
        Assert.Contains("write-protected", (await Call(tools, "move_file", new { path = "A.cs", new_path = "Recipes/A.cs", expected_sha256 = a })).Content);
        Assert.Contains("protection rules", (await Call(tools, "write_file", new { path = ".slcopilot/settings.json", content = "{}", expected_sha256 = "x" })).Content);
        Assert.Equal("{}", System.IO.File.ReadAllText(Path.Combine(Root, "Recipes", "r.json")));
        Assert.Equal(0, tools.ChangeCount);
    }

    [Fact]
    public async Task ScopedInstructionsAreAttachedOnceToMatchingFiles()
    {
        Write(".slcopilot/instructions/workflow.md", "---\napplyTo: src/Workflow/**\n---\n工作流节点不得吞异常。");
        Write(".github/instructions/xaml.instructions.md", "---\napplyTo: \"*.xaml\"\n---\nXAML 文本用单引号。");
        Write(".slcopilot/instructions/none.md", "没有 applyTo 的文件不生效。");
        Write("src/Workflow/Node.cs", "class Node { }");
        Write("src/Other.cs", "class Other { }");
        Write("Views/Main.xaml", "<Grid/>");
        var tools = Workspace();
        Assert.Equal(2, tools.ScopedInstructions.Count);

        var other = await Call(tools, "read_file", new { path = "src/Other.cs" });
        Assert.DoesNotContain("instructions", other.Content);
        var node = await Call(tools, "read_file", new { path = "src/Workflow/Node.cs" });
        Assert.Contains("工作流节点不得吞异常", node.Content);
        Assert.Contains("附项目规则", ToolSummary.Describe("read_file", node.Content));
        var again = await Call(tools, "read_file", new { path = "src/Workflow/Node.cs" });
        Assert.DoesNotContain("工作流节点不得吞异常", again.Content);
        Assert.Contains("XAML 文本用单引号", (await Call(tools, "read_file", new { path = "Views/Main.xaml" })).Content);
    }

    [Fact]
    public void UserInstructionsUseTheirOwnBlock()
    {
        var path = Path.Combine(temp, "user.md");
        System.IO.File.WriteAllText(path, "中文回答。");
        var user = ProjectInstructions.LoadUser(path)!;
        Assert.True(user.IsUser);
        Assert.Contains("<user_instructions>\n中文回答。\n</user_instructions>", user.ToPromptBlock());
        Assert.Null(ProjectInstructions.LoadUser(Path.Combine(temp, "missing.md")));
    }

    [Fact]
    public async Task ToolResultsKeepChineseReadable()
    {
        Write("C.cs", "// 中文注释 \"引号\" \\u0041\nclass C { }\n");
        var read = await Call(Workspace(), "read_file", new { path = "C.cs" });
        Assert.Contains("// 中文注释", read.Content);
        Assert.DoesNotContain("\\u4E2D", read.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("1\t// 中文注释 \"引号\" \\u0041\n2\tclass C { }", JsonDocument.Parse(read.Content).RootElement.GetProperty("content").GetString());
    }
}

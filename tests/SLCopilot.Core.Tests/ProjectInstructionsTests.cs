using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class ProjectInstructionsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SLCopilot-instructions-" + Guid.NewGuid().ToString("N"));
    public ProjectInstructionsTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text);
    }

    [Fact]
    public void SlcopilotFileWinsOverCopilotFileAndMissingFilesReturnNull()
    {
        Assert.Null(ProjectInstructions.Load(root));
        Write(".github/copilot-instructions.md", "Use tabs.");
        Assert.Equal(".github/copilot-instructions.md", ProjectInstructions.Load(root)!.RelativePath);
        Write(".slcopilot/instructions.md", "  中文注释，禁止 async void。 ");
        var loaded = ProjectInstructions.Load(root)!;
        Assert.Equal(".slcopilot/instructions.md", loaded.RelativePath);
        Assert.Equal("中文注释，禁止 async void。", loaded.Text);
        Assert.Contains("<project_instructions>", loaded.ToPromptBlock());
    }

    [Fact]
    public void IgnoredInstructionFilesAreNotSent()
    {
        Write(".slcopilot/instructions.md", "secret rules");
        Write(".slcopilotignore", ".slcopilot/\n");
        Assert.Null(ProjectInstructions.Load(root));
    }

    [Fact]
    public void LongInstructionsAreTruncated()
    {
        Write(".slcopilot/instructions.md", new string('x', ProjectInstructions.MaxChars + 100));
        Assert.EndsWith("[Instructions truncated]", ProjectInstructions.Load(root)!.Text);
    }

    [Fact]
    public void RememberCreatesOrExtendsTheInstructionsFile()
    {
        Assert.Equal(".slcopilot/instructions.md", ProjectInstructions.Remember(root, "日志用 NLog"));
        Assert.Equal("# 项目指令\n\n- 日志用 NLog\n", System.IO.File.ReadAllText(Path.Combine(root, ".slcopilot", "instructions.md")));
        ProjectInstructions.Remember(root, "  不要用 Console\r\n输出  ");
        Assert.EndsWith("- 日志用 NLog\n- 不要用 Console 输出\n", System.IO.File.ReadAllText(Path.Combine(root, ".slcopilot", "instructions.md")));
        Assert.Contains("不要用 Console 输出", ProjectInstructions.Load(root)!.Text);
        Assert.Throws<ArgumentException>(() => ProjectInstructions.Remember(root, "  "));
    }
}

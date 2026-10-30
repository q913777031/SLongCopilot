using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class CustomCommandsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SLCopilot-commands-" + Guid.NewGuid().ToString("N"));
    public CustomCommandsTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text);
    }

    [Fact]
    public void LoadsBothLocationsWithFrontMatterAndSkipsInvalidOrDuplicateNames()
    {
        Write(".slcopilot/commands/halcon-check.md", "---\ndescription: 检查 HObject 释放\nmode: plan\n---\n检查 $ARGUMENTS 中的 HObject 是否都已释放。");
        Write(".slcopilot/commands/fix.md", "覆盖内置命令（应跳过）");
        Write(".slcopilot/commands/bad name.md", "名称不合法");
        Write(".slcopilot/commands/empty.md", "---\nmode: agent\n---\n");
        Write(".github/prompts/halcon-check.prompt.md", "同名，.slcopilot 优先");
        Write(".github/prompts/wpf.prompt.md", "---\nmode: edit\n---\n# 检查绑定\n检查 XAML 绑定错误。");
        Write(".github/prompts/notes.md", "不是 .prompt.md，跳过");

        var commands = CustomCommands.Load(root);
        Assert.Equal(["halcon-check", "wpf"], commands.Select(c => c.Name));
        var halcon = commands[0];
        Assert.Equal(RunMode.Plan, halcon.Mode);
        Assert.Equal(".slcopilot/commands/halcon-check.md", halcon.Source);
        Assert.StartsWith("检查 HObject 释放", halcon.Description);
        Assert.Equal(RunMode.Agent, commands[1].Mode);
        Assert.StartsWith("检查绑定", commands[1].Description);

        var parsed = PromptCommands.Parse("/Halcon-Check CameraService.cs", commands);
        Assert.Same(halcon, parsed.Command);
        Assert.Equal("检查 CameraService.cs 中的 HObject 是否都已释放。", parsed.Text);
        Assert.Equal(RunMode.Agent, PromptCommands.Parse("/fix x", commands).Command!.Mode);
        Assert.Null(PromptCommands.Parse("/wpf-x", commands).Command);
    }

    [Fact]
    public void MissingDirectoriesAndInvalidRootsReturnEmpty()
    {
        Assert.Empty(CustomCommands.Load(root));
        Assert.Empty(CustomCommands.Load(Path.Combine(root, "missing")));
    }
}

public sealed class DebugReportTests
{
    [Fact]
    public void FormatsExceptionFramesAndLocalsWithLimitsAndRelativePaths()
    {
        var frames = Enumerable.Range(0, DebugReport.MaxFrames + 1)
            .Select(i => new DebugFrame($"App.Method{i}()", i == 0 ? @"C:\src\App\Camera.cs" : null, 42, "App.dll")).ToList();
        var snapshot = new DebugSnapshot("System.NullReferenceException", "\"Object reference not set\\r\\nto an instance.\"",
            "\"   at App.Camera.Grab() in C:\\\\src\\\\App\\\\Camera.cs:line 42\"", "null", frames,
            [new("image", "HalconDotNet.HObject", "null"), new("text", "string", new string('x', 1000))]);
        var text = DebugReport.Format(snapshot, @"C:\src");
        Assert.Contains("异常：System.NullReferenceException", text);
        Assert.Contains("Object reference not set\nto an instance.", text);
        Assert.Contains(@"in App\Camera.cs:line 42", text);
        Assert.Contains(@"App.Method0()  @ App\Camera.cs:42", text);
        Assert.Contains("App.Method1()  [App.dll]", text);
        Assert.Contains("更多帧已省略", text);
        Assert.DoesNotContain("内部异常", text);
        Assert.Contains("image (HalconDotNet.HObject) = null", text);
        Assert.DoesNotContain(new string('x', DebugReport.MaxValueChars + 1), text);
    }

    [Fact]
    public void BreakpointWithoutExceptionSaysSo()
    {
        var text = DebugReport.Format(new(null, null, null, null, [], []), null);
        Assert.Contains("没有当前异常", text);
        Assert.Null(DebugReport.Unquote("null"));
        Assert.Equal("plain", DebugReport.Unquote("plain"));
    }

    [Fact]
    public void ReviewRequestKeepsDiffAsDataAndAddsExtraRequirements()
    {
        var text = CodeReview.Build("暂存区的 Git 改动", "diff --git a/A.cs b/A.cs", truncated: true, extra: "重点看线程");
        Assert.Contains("不要修改文件", text);
        Assert.Contains("额外要求：重点看线程", text);
        Assert.Contains("<diff>\ndiff --git a/A.cs b/A.cs\n</diff>", text);
        Assert.Contains("truncated", text);
    }
}

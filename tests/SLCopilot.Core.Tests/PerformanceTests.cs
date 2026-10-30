using System.Text.Json;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

/// <summary>遍历不逐级检查上级目录、搜索分批并行读取、重复整读未变文件只回复“未变化”。</summary>
public sealed class PerformanceTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "SLCopilot-perf-tests-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(temp, "project");

    public PerformanceTests() => Directory.CreateDirectory(Root);
    public void Dispose() { try { Directory.Delete(temp, true); } catch (IOException) { } }

    private WorkspaceTools Workspace(bool dedup = false) => new(Root, true, Path.Combine(temp, "backups")) { DeduplicateReads = dedup };

    private static async Task<JsonElement> Call(WorkspaceTools tools, string name, object args)
    {
        var result = await tools.ExecuteAsync(name, JsonSerializer.SerializeToElement(args), CancellationToken.None);
        Assert.False(result.IsError, result.Content);
        return JsonDocument.Parse(result.Content).RootElement;
    }

    private void Write(string relative, string text)
    {
        var full = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, text);
    }

    [Fact]
    public void WalkEntriesFollowTheSamePathRulesWithoutTouchingTheDisk()
    {
        var policy = new WorkspacePolicy(Root);
        Assert.True(policy.AllowsWalkEntry("src/A.cs", FileAttributes.Normal));
        Assert.True(policy.AllowsWalkEntry("src", FileAttributes.Directory));
        Assert.False(policy.AllowsWalkEntry("src/bin", FileAttributes.Directory));
        Assert.False(policy.AllowsWalkEntry("src/App.dll", FileAttributes.Normal));
        Assert.False(policy.AllowsWalkEntry("src/secrets.json", FileAttributes.Normal));
        Assert.False(policy.AllowsWalkEntry("src/Linked", FileAttributes.Directory | FileAttributes.ReparsePoint));
        Assert.False(policy.AllowsWalkEntry("src/A.cs", FileAttributes.ReparsePoint));
    }

    [Fact]
    public async Task FindFilesSkipsExcludedDirectoriesAndLinks()
    {
        Write("src/A.cs", "class A {}");
        Write("src/obj/Gen.cs", "class Gen {}");
        Write("outside/B.cs", "class B {}");
#if NET
        try { Directory.CreateSymbolicLink(Path.Combine(Root, "src", "Linked"), Path.Combine(Root, "outside")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { /* 无权创建链接的 Windows 账户：只验证排除目录。 */ }
#endif
        var files = (await Call(Workspace(), "find_files", new { path = "src", pattern = "*.cs" })).GetProperty("files").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Equal(["src/A.cs"], files);
    }

    [Fact]
    public async Task SearchKeepsWalkOrderAndTruncatesAcrossParallelBatches()
    {
        if (!Compat.IsWindows) return; // 文件读取目前只支持 Windows（FileSafety）。
        for (var i = 0; i < 30; i++) Write($"src/F{i:00}.cs", string.Join("\n", Enumerable.Range(0, 10).Select(n => $"// needle {i} {n}")));
        var result = await Call(Workspace(), "search_files", new { path = "src", query = "needle" });
        var matches = result.GetProperty("matches").EnumerateArray().Select(m => m.GetString()!).ToList();
        Assert.Equal(200, matches.Count);
        Assert.True(result.GetProperty("truncated").GetBoolean());
        var direct = Directory.EnumerateFiles(Path.Combine(Root, "src")).Select(f => Path.GetFileName(f)!).Take(20).ToList();
        Assert.Equal(direct, matches.Select(m => m.Split(':')[0].Substring(4)).Distinct().ToList());
    }

    [Fact]
    public async Task UnchangedWholeFileIsNotSentTwice()
    {
        if (!Compat.IsWindows) return; // 文件读取目前只支持 Windows（FileSafety）。
        Write("A.cs", "class A {}\n");
        var workspace = Workspace(dedup: true);
        var first = await Call(workspace, "read_file", new { path = "A.cs" });
        Assert.True(first.TryGetProperty("content", out _));
        var second = await Call(workspace, "read_file", new { path = "A.cs" });
        Assert.True(second.GetProperty("unchanged").GetBoolean());
        Assert.False(second.TryGetProperty("content", out _));
        Assert.Equal(first.GetProperty("sha256").GetString(), second.GetProperty("sha256").GetString());

        System.IO.File.WriteAllText(Path.Combine(Root, "A.cs"), "class A { int x; }\n");
        Assert.True((await Call(workspace, "read_file", new { path = "A.cs" })).TryGetProperty("content", out _));
        workspace.ForgetReturnedReads();
        Assert.True((await Call(workspace, "read_file", new { path = "A.cs" })).TryGetProperty("content", out _));
        Assert.True((await Call(Workspace(dedup: false), "read_file", new { path = "A.cs" })).TryGetProperty("content", out _));
    }

    [Fact]
    public void UnchangedReadsAreSummarized()
        => Assert.Equal("未变化，未重复发送", ToolSummary.Describe("read_file", "{\"path\":\"A.cs\",\"sha256\":\"x\",\"total_lines\":3,\"unchanged\":true}"));
}

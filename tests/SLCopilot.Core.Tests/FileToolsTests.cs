using System.Text.Json;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

/// <summary>0.5.0 新增的工作区工具：删除、移动、按模式查找、正则 / 通配搜索，以及命令工具的模式约束。</summary>
public sealed class FileToolsTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "SLCopilot-file-tools-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(temp, "project");
    private string Backups => Path.Combine(temp, "backups");
    public FileToolsTests() => Directory.CreateDirectory(Root);
    private WorkspaceTools Workspace(bool writes = true, Func<WriteProposal, CancellationToken, Task<bool>>? confirm = null, CommandRunner? commands = null)
        => new(Root, writes, Backups, null, confirm, commands: commands);
    private static Task<ToolResult> Call(WorkspaceTools tools, string name, object args)
        => tools.ExecuteAsync(name, JsonSerializer.SerializeToElement(args), CancellationToken.None);
    private static string Hash(ToolResult result) => JsonDocument.Parse(result.Content).RootElement.GetProperty("sha256").GetString()!;
    private string FilePath(string name) => Path.Combine(Root, name);

    private async Task<string> ReadHash(WorkspaceTools workspace, string path)
    {
        var read = await Call(workspace, "read_file", new { path });
        Assert.False(read.IsError, read.Content);
        return Hash(read);
    }

    [Fact]
    public async Task DeleteNeedsReadAndApprovalAndUndoRecreatesTheFile()
    {
        var original = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\r', (byte)'\n' };
        await File.WriteAllBytesAsync(FilePath("A.cs"), original);
        WriteProposal? proposal = null;
        var workspace = Workspace(confirm: (p, _) => { proposal = p; return Task.FromResult(true); });
        Assert.True((await Call(workspace, "delete_file", new { path = "A.cs", expected_sha256 = "x" })).IsError);
        var deleted = await Call(workspace, "delete_file", new { path = "A.cs", expected_sha256 = await ReadHash(workspace, "A.cs") });
        Assert.False(deleted.IsError, deleted.Content);
        Assert.Equal(ProposalKind.Delete, proposal!.Kind);
        Assert.Contains("-a", proposal.Diff);
        Assert.False(File.Exists(FilePath("A.cs")));
        Assert.Contains("Deleted: A.cs", workspace.DescribeChanges());
        await workspace.UndoAsync(CancellationToken.None);
        Assert.Equal(original, await File.ReadAllBytesAsync(FilePath("A.cs")));
        Assert.Equal(0, workspace.ChangeCount);
    }

    [Fact]
    public async Task RejectedDeleteKeepsTheFileAndCreatesNoBackup()
    {
        await File.WriteAllTextAsync(FilePath("A.cs"), "a");
        var workspace = Workspace(confirm: (_, _) => Task.FromResult(false));
        var result = await Call(workspace, "delete_file", new { path = "A.cs", expected_sha256 = await ReadHash(workspace, "A.cs") });
        Assert.True(result.IsError);
        Assert.True(File.Exists(FilePath("A.cs")));
        Assert.Equal(0, workspace.ChangeCount);
        Assert.False(Directory.Exists(Backups));
    }

    [Fact]
    public async Task DeleteIsRefusedInReadOnlyRunsAndWhenTheFileChanged()
    {
        await File.WriteAllTextAsync(FilePath("A.cs"), "a");
        var readOnly = Workspace(writes: false);
        Assert.True((await Call(readOnly, "delete_file", new { path = "A.cs", expected_sha256 = await ReadHash(readOnly, "A.cs") })).IsError);
        var workspace = Workspace();
        var hash = await ReadHash(workspace, "A.cs");
        await File.WriteAllTextAsync(FilePath("A.cs"), "user edit");
        Assert.True((await Call(workspace, "delete_file", new { path = "A.cs", expected_sha256 = hash })).IsError);
        Assert.Equal("user edit", await File.ReadAllTextAsync(FilePath("A.cs")));
    }

    [Fact]
    public async Task UndoDoesNotOverwriteAFileRecreatedByTheUser()
    {
        await File.WriteAllTextAsync(FilePath("A.cs"), "a");
        var workspace = Workspace();
        await Call(workspace, "delete_file", new { path = "A.cs", expected_sha256 = await ReadHash(workspace, "A.cs") });
        await File.WriteAllTextAsync(FilePath("A.cs"), "user");
        await Assert.ThrowsAsync<WorkspaceException>(() => workspace.UndoAsync(CancellationToken.None));
        Assert.Equal("user", await File.ReadAllTextAsync(FilePath("A.cs")));
    }

    [Fact]
    public async Task RecreatingADeletedFileKeepsTheOriginalBackup()
    {
        await File.WriteAllTextAsync(FilePath("A.cs"), "original");
        var workspace = Workspace();
        await Call(workspace, "delete_file", new { path = "A.cs", expected_sha256 = await ReadHash(workspace, "A.cs") });
        var created = await Call(workspace, "write_file", new { path = "A.cs", content = "rewritten", expected_sha256 = "new" });
        Assert.False(created.IsError, created.Content);
        Assert.Contains("Modified: A.cs", workspace.DescribeChanges());
        await workspace.UndoAsync(CancellationToken.None);
        Assert.Equal("original", await File.ReadAllTextAsync(FilePath("A.cs")));
    }

    [Fact]
    public async Task AllowForRunSkipsLaterApprovalsForThatFileOnly()
    {
        await File.WriteAllTextAsync(FilePath("A.cs"), "a0");
        await File.WriteAllTextAsync(FilePath("B.cs"), "b0");
        var asked = new List<string>();
        var autoApproved = new List<string>();
        Func<WriteProposal, CancellationToken, Task<bool>> confirm = (p, _) =>
        {
            asked.Add($"{p.Kind}:{p.RelativePath}");
            p.AllowForRun = true;
            return Task.FromResult(true);
        };
        var workspace = Workspace(confirm: confirm);
        workspace.WriteAutoApproved += autoApproved.Add;
        var first = await Call(workspace, "write_file", new { path = "A.cs", content = "a1", expected_sha256 = await ReadHash(workspace, "A.cs") });
        Assert.False(first.IsError, first.Content);
        var second = await Call(workspace, "edit_file", new { path = "A.cs", old_text = "a1", new_text = "a2", expected_sha256 = Hash(first) });
        Assert.False(second.IsError, second.Content);
        Assert.Equal("a2", await File.ReadAllTextAsync(FilePath("A.cs")));
        Assert.Equal(new[] { "A.cs" }, autoApproved);
        // 其他文件仍需批准；删除与移动始终逐次批准，即使用户曾“始终允许”该文件。
        await Call(workspace, "write_file", new { path = "B.cs", content = "b1", expected_sha256 = await ReadHash(workspace, "B.cs") });
        await Call(workspace, "delete_file", new { path = "A.cs", expected_sha256 = Hash(second) });
        Assert.Equal(new[] { "Write:A.cs", "Write:B.cs", "Delete:A.cs" }, asked);
        // 版本检查不因自动批准而跳过。
        Assert.True((await Call(workspace, "write_file", new { path = "B.cs", content = "b2", expected_sha256 = "stale" })).IsError);
        // 新的运行（新的 WorkspaceTools）重新询问。
        var next = Workspace(confirm: confirm);
        await Call(next, "write_file", new { path = "B.cs", content = "b3", expected_sha256 = await ReadHash(next, "B.cs") });
        Assert.Equal("Write:B.cs", asked[^1]);
        Assert.Equal(4, asked.Count);
    }

    [Fact]
    public async Task AllowForRunIsIgnoredWhenTheWriteIsRejected()
    {
        await File.WriteAllTextAsync(FilePath("A.cs"), "a0");
        var answers = new Queue<bool>(new[] { false, true });
        var asked = 0;
        var workspace = Workspace(confirm: (p, _) => { asked++; p.AllowForRun = true; return Task.FromResult(answers.Dequeue()); });
        var hash = await ReadHash(workspace, "A.cs");
        Assert.True((await Call(workspace, "write_file", new { path = "A.cs", content = "a1", expected_sha256 = hash })).IsError);
        Assert.False((await Call(workspace, "write_file", new { path = "A.cs", content = "a1", expected_sha256 = hash })).IsError);
        Assert.Equal(2, asked);
    }

    [Theory]
    [InlineData("", "a\nb\n", 2, 0)]
    [InlineData("a\nb\n", "", 0, 2)]
    [InlineData("a\nb\nc\n", "a\nB\nc\n", 1, 1)]
    [InlineData("a\r\nb\r\n", "a\nb\n", 0, 0)]
    [InlineData("1\n2\n3\n4\n", "1\nx\n2\n4\ny\n", 2, 1)]
    public void CountLinesUsesLongestCommonSubsequence(string before, string after, int added, int removed)
        => Assert.Equal((added, removed), ChangePreview.CountLines(before, after));

    [Fact]
    public async Task ChangedFilesReportKindsAndLineCounts()
    {
        await File.WriteAllTextAsync(FilePath("A.cs"), "one\ntwo\n");
        await File.WriteAllTextAsync(FilePath("B.cs"), "b\n");
        await File.WriteAllTextAsync(FilePath("C.cs"), "c\n");
        var workspace = Workspace();
        await Call(workspace, "edit_file", new { path = "A.cs", old_text = "two", new_text = "2\nthree", expected_sha256 = await ReadHash(workspace, "A.cs") });
        await Call(workspace, "write_file", new { path = "New.cs", content = "x\ny\nz\n", expected_sha256 = "new" });
        await Call(workspace, "delete_file", new { path = "B.cs", expected_sha256 = await ReadHash(workspace, "B.cs") });
        await Call(workspace, "move_file", new { path = "C.cs", new_path = "D.cs", expected_sha256 = await ReadHash(workspace, "C.cs") });
        var files = workspace.ChangedFiles.ToDictionary(f => f.Path);
        Assert.Equal(("modified", 2, 1), (files["A.cs"].Kind, files["A.cs"].Added, files["A.cs"].Removed));
        Assert.Equal(("created", 3, 0), (files["New.cs"].Kind, files["New.cs"].Added, files["New.cs"].Removed));
        Assert.Equal(("deleted", 0, 1), (files["B.cs"].Kind, files["B.cs"].Added, files["B.cs"].Removed));
        Assert.Equal(("deleted", "created"), (files["C.cs"].Kind, files["D.cs"].Kind));
        Assert.All(files.Values, f => Assert.True(f.Complete));
        Assert.Equal("b\n", await File.ReadAllTextAsync(files["B.cs"].BackupPath));
        Assert.Equal(FilePath("A.cs"), files["A.cs"].FullPath);
        // 快照可序列化后在界面还原（AgentRunner 以 changed_files 事件发送）。
        var restored = JsonSerializer.Deserialize<List<ChangedFile>>(JsonSerializer.Serialize(workspace.ChangedFiles))!;
        Assert.Equal(workspace.ChangedFiles, restored);
        await workspace.UndoAsync(CancellationToken.None);
        Assert.Empty(workspace.ChangedFiles);
    }

    [Fact]
    public async Task MoveCopiesBytesDeletesSourceAndUndoRestoresBoth()
    {
        Directory.CreateDirectory(FilePath("src"));
        await File.WriteAllTextAsync(FilePath("src/Old.cs"), "class Old {}\r\n");
        WriteProposal? proposal = null;
        var workspace = Workspace(confirm: (p, _) => { proposal = p; return Task.FromResult(true); });
        var moved = await Call(workspace, "move_file", new { path = "src/Old.cs", new_path = "src/Sub/New.cs", expected_sha256 = await ReadHash(workspace, "src/Old.cs") });
        Assert.False(moved.IsError, moved.Content);
        Assert.Equal(ProposalKind.Move, proposal!.Kind);
        Assert.Equal("src/Sub/New.cs", proposal.Destination);
        Assert.False(File.Exists(FilePath("src/Old.cs")));
        Assert.Equal("class Old {}\r\n", await File.ReadAllTextAsync(FilePath("src/Sub/New.cs")));
        Assert.Equal(2, workspace.ChangeCount);
        // 移动后的文件可直接编辑（版本已登记）。
        var edited = await Call(workspace, "edit_file", new { path = "src/Sub/New.cs", old_text = "Old", new_text = "New", expected_sha256 = Hash(moved) });
        Assert.False(edited.IsError, edited.Content);
        await workspace.UndoAsync(CancellationToken.None);
        Assert.Equal("class Old {}\r\n", await File.ReadAllTextAsync(FilePath("src/Old.cs")));
        Assert.False(File.Exists(FilePath("src/Sub/New.cs")));
    }

    [Fact]
    public async Task MoveRefusesExistingDestinationAndSamePath()
    {
        await File.WriteAllTextAsync(FilePath("A.cs"), "a");
        await File.WriteAllTextAsync(FilePath("B.cs"), "b");
        var workspace = Workspace();
        var hash = await ReadHash(workspace, "A.cs");
        Assert.True((await Call(workspace, "move_file", new { path = "A.cs", new_path = "B.cs", expected_sha256 = hash })).IsError);
        Assert.True((await Call(workspace, "move_file", new { path = "A.cs", new_path = "a.cs", expected_sha256 = hash })).IsError);
        Assert.True((await Call(workspace, "move_file", new { path = "A.cs", new_path = ".env", expected_sha256 = hash })).IsError);
        Assert.Equal("a", await File.ReadAllTextAsync(FilePath("A.cs")));
        Assert.Equal("b", await File.ReadAllTextAsync(FilePath("B.cs")));
        Assert.Equal(0, workspace.ChangeCount);
    }

    [Fact]
    public async Task FindFilesMatchesNamesOrPathsAndRespectsPolicy()
    {
        Directory.CreateDirectory(FilePath("src/Views"));
        Directory.CreateDirectory(FilePath("bin"));
        await File.WriteAllTextAsync(FilePath("src/OrderService.cs"), "");
        await File.WriteAllTextAsync(FilePath("src/Views/Main.xaml"), "");
        await File.WriteAllTextAsync(FilePath("bin/Hidden.cs"), "");
        await File.WriteAllTextAsync(FilePath("App.csproj"), "");
        var workspace = Workspace(writes: false);
        var byName = JsonDocument.Parse((await Call(workspace, "find_files", new { path = ".", pattern = "*.cs" })).Content).RootElement.GetProperty("files");
        Assert.Equal(new[] { "src/OrderService.cs" }, byName.EnumerateArray().Select(f => f.GetString()).ToArray());
        var byPath = (await Call(workspace, "find_files", new { path = ".", pattern = "src/**/*.xaml" })).Content;
        Assert.Contains("src/Views/Main.xaml", byPath);
        var scoped = (await Call(workspace, "find_files", new { path = "src", pattern = "Views/*.xaml" })).Content;
        Assert.Contains("src/Views/Main.xaml", scoped);
        Assert.True((await Call(workspace, "find_files", new { path = ".", pattern = "" })).IsError);
    }

    [Fact]
    public async Task SearchSupportsRegexGlobAndReportsTruncation()
    {
        await File.WriteAllTextAsync(FilePath("A.cs"), "int Count = 1;\r\nvar total = Count + 2;\r\n");
        await File.WriteAllTextAsync(FilePath("B.xaml"), "<TextBlock Text=\"Count\"/>");
        await File.WriteAllTextAsync(FilePath("Many.cs"), string.Join("\n", Enumerable.Range(0, 300).Select(i => "hit " + i)));
        var workspace = Workspace(writes: false);
        var regex = (await Call(workspace, "search_files", new { path = ".", query = "^int\\s+count", regex = true })).Content;
        Assert.Contains("A.cs:1:", regex);
        Assert.DoesNotContain("A.cs:2:", regex);
        var glob = (await Call(workspace, "search_files", new { path = ".", query = "count", glob = "*.xaml" })).Content;
        Assert.Contains("B.xaml:1:", glob);
        Assert.DoesNotContain("A.cs", glob);
        Assert.True((await Call(workspace, "search_files", new { path = ".", query = "(", regex = true })).IsError);
        var many = JsonDocument.Parse((await Call(workspace, "search_files", new { path = ".", query = "hit" })).Content).RootElement;
        Assert.Equal(50, many.GetProperty("matches").GetArrayLength());
        for (var i = 0; i < 5; i++) await File.WriteAllTextAsync(FilePath($"More{i}.cs"), string.Join("\n", Enumerable.Range(0, 60).Select(n => "hit " + n)));
        var capped = JsonDocument.Parse((await Call(workspace, "search_files", new { path = ".", query = "hit" })).Content).RootElement;
        Assert.Equal(200, capped.GetProperty("matches").GetArrayLength());
        Assert.True(capped.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task CommandToolsNeedAgentModeAndAHostRunner()
    {
        var runner = new CommandRunner(["dotnet --version"], (_, _, _) => Task.FromResult(true));
        var readOnly = await Call(Workspace(writes: false, commands: runner), "run_command", new { command = "dotnet", args = new[] { "--version" } });
        Assert.True(readOnly.IsError);
        Assert.Contains("Agent mode", readOnly.Content);
        Assert.True((await Call(Workspace(), "run_tests", new { })).IsError);
        Assert.True((await Call(Workspace(commands: runner), "run_command", new { command = "dotnet", args = "--version" })).IsError);
        Assert.True((await Call(Workspace(commands: runner), "run_command", new { command = "dotnet", args = new[] { "--version" }, cwd = "../" })).IsError);
        var result = await Call(Workspace(commands: runner), "run_command", new { command = "dotnet", args = new[] { "--version" } });
        Assert.False(result.IsError, result.Content);
        var json = JsonDocument.Parse(result.Content).RootElement;
        Assert.True(json.GetProperty("succeeded").GetBoolean());
        Assert.Equal(0, json.GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public async Task RunTestsWithoutResultsIsNotReportedAsSuccess()
    {
        // 空目录中 dotnet test 找不到项目：不能报告成功。
        var runner = new CommandRunner(["dotnet test"], (_, _, _) => Task.FromResult(true));
        var result = await Call(Workspace(commands: runner), "run_tests", new { timeout_seconds = 120 });
        Assert.False(result.IsError, result.Content);
        var json = JsonDocument.Parse(result.Content).RootElement;
        Assert.False(json.GetProperty("succeeded").GetBoolean());
        Assert.Equal(0, json.GetProperty("total").GetInt32());
        Assert.Contains("No test results", json.GetProperty("note").GetString());
        Assert.True((await Call(Workspace(commands: runner), "run_tests", new { project = "missing.csproj" })).IsError);
        Assert.True((await Call(Workspace(commands: runner), "run_tests", new { project = "notes.txt" })).IsError);
    }

    public void Dispose()
    {
        if (Path.GetFullPath(temp).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            Directory.Delete(temp, recursive: true);
    }
}

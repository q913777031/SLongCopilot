using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class WorkspaceTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "SLCopilot-agent-tests-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(temp, "project");
    private string Backups => Path.Combine(temp, "backups");
    public WorkspaceTests() => Directory.CreateDirectory(Root);
    private WorkspaceTools Workspace(bool writes = true, Func<string, CancellationToken, Task>? guard = null) => new(Root, writes, Backups, guard);
    private static JsonElement Args(object args) => JsonSerializer.SerializeToElement(args);
    private static Task<ToolResult> Call(WorkspaceTools tools, string name, object args) => tools.ExecuteAsync(name, Args(args), CancellationToken.None);
    private static string Hash(ToolResult result) => JsonDocument.Parse(result.Content).RootElement.GetProperty("sha256").GetString()!;
    private string FilePath(string name) => Path.Combine(Root, name);

    [Fact]
    public async Task ReadEditWriteAndUndoRestoreOriginalBytes()
    {
        var original = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("class A {}\r\n")).ToArray();
        await File.WriteAllBytesAsync(FilePath("A.cs"), original);
        var workspace = Workspace();
        var read = await Call(workspace, "read_file", new { path = "A.cs" });
        Assert.False(read.IsError);
        var edited = await Call(workspace, "edit_file", new { path = "A.cs", old_text = "class A", new_text = "class B", expected_sha256 = Hash(read) });
        Assert.False(edited.IsError, edited.Content);
        Assert.Equal("class B {}\r\n", await File.ReadAllTextAsync(FilePath("A.cs")));
        Assert.True((await File.ReadAllBytesAsync(FilePath("A.cs"))).AsSpan().StartsWith(new byte[] { 239, 187, 191 }));
        var again = await Call(workspace, "write_file", new { path = "A.cs", content = "class C {}\r\n", expected_sha256 = Hash(edited) });
        Assert.False(again.IsError, again.Content);
        Assert.Equal(1, workspace.ChangeCount);
        Assert.Contains("-class A", workspace.DescribeDiffs());
        Assert.Contains("+class C", workspace.DescribeDiffs());
        Assert.True(File.Exists(Path.Combine(workspace.BackupDirectory, "manifest.json")));
        await workspace.UndoAsync(CancellationToken.None);
        Assert.Equal(original, await File.ReadAllBytesAsync(FilePath("A.cs")));
        Assert.Equal(0, workspace.ChangeCount);
    }

    [Fact]
    public async Task NewFilesAreActuallyWrittenAndUndoRemovesThem()
    {
        var workspace = Workspace();
        var result = await Call(workspace, "write_file", new { path = "src/New.cs", content = "// 中文\n", expected_sha256 = "new" });
        Assert.False(result.IsError, result.Content);
        Assert.Equal("// 中文\n", await File.ReadAllTextAsync(FilePath("src/New.cs")));
        await workspace.UndoAsync(CancellationToken.None);
        Assert.False(File.Exists(FilePath("src/New.cs")));
    }

    [Fact]
    public async Task ReadOnlyModeCannotWriteOrCreateDirectories()
    {
        var result = await Call(Workspace(false), "write_file", new { path = "new/File.cs", content = "x", expected_sha256 = "new" });
        Assert.True(result.IsError);
        Assert.False(Directory.Exists(FilePath("new")));
        Assert.False(Directory.Exists(Backups));
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("..\\outside.cs")]
    [InlineData("C:\\outside.cs")]
    [InlineData("/outside.cs")]
    [InlineData("file.cs:stream")]
    [InlineData("file.cs.")]
    [InlineData("CON.cs")]
    [InlineData(".git/config")]
    [InlineData(".env")]
    [InlineData(".env.production")]
    [InlineData("src/secrets.json")]
    [InlineData("node_modules/file.js")]
    [InlineData("private.pem")]
    [InlineData(".slcopilotignore")]
    public async Task UnsafePathsCannotBeReadOrWritten(string path)
    {
        var workspace = Workspace();
        Assert.True((await Call(workspace, "read_file", new { path })).IsError);
        Assert.True((await Call(workspace, "write_file", new { path, content = "x", expected_sha256 = "new" })).IsError);
        Assert.Equal(0, workspace.ChangeCount);
    }

    [Fact]
    public async Task ExistingFilesRequireReadAndStaleHashesAreRejected()
    {
        await File.WriteAllTextAsync(FilePath("A.cs"), "one");
        var workspace = Workspace();
        Assert.True((await Call(workspace, "write_file", new { path = "A.cs", content = "two", expected_sha256 = "new" })).IsError);
        var read = await Call(workspace, "read_file", new { path = "A.cs" });
        await File.WriteAllTextAsync(FilePath("A.cs"), "user edit");
        Assert.True((await Call(workspace, "write_file", new { path = "A.cs", content = "two", expected_sha256 = Hash(read) })).IsError);
        Assert.Equal("user edit", await File.ReadAllTextAsync(FilePath("A.cs")));
        Assert.Equal(0, workspace.ChangeCount);
    }

    [Fact]
    public async Task UndoRefusesSubsequentUserChangesBeforeRestoringAnyFile()
    {
        var workspace = Workspace();
        await Call(workspace, "write_file", new { path = "A.cs", content = "a", expected_sha256 = "new" });
        await Call(workspace, "write_file", new { path = "B.cs", content = "b", expected_sha256 = "new" });
        await File.WriteAllTextAsync(FilePath("B.cs"), "user");
        await Assert.ThrowsAsync<WorkspaceException>(() => workspace.UndoAsync(CancellationToken.None));
        Assert.Equal("a", await File.ReadAllTextAsync(FilePath("A.cs")));
        Assert.Equal("user", await File.ReadAllTextAsync(FilePath("B.cs")));
    }

    [Fact]
    public async Task EditorGuardBlocksMutationAndUndo()
    {
        var blocked = false;
        var workspace = Workspace(guard: (_, _) => blocked ? Task.FromException(new WorkspaceException("Unsaved editor changes.")) : Task.CompletedTask);
        blocked = true;
        var result = await Call(workspace, "write_file", new { path = "A.cs", content = "x", expected_sha256 = "new" });
        Assert.True(result.IsError);
        Assert.False(File.Exists(FilePath("A.cs")));
        blocked = false;
        Assert.False((await Call(workspace, "write_file", new { path = "A.cs", content = "x", expected_sha256 = "new" })).IsError);
        blocked = true;
        await Assert.ThrowsAsync<WorkspaceException>(() => workspace.UndoAsync(CancellationToken.None));
        Assert.True(File.Exists(FilePath("A.cs")));
    }

    [Fact]
    public async Task EditRequiresExactlyOneMatch()
    {
        await File.WriteAllTextAsync(FilePath("A.cs"), "same same");
        var workspace = Workspace();
        var read = await Call(workspace, "read_file", new { path = "A.cs" });
        var result = await Call(workspace, "edit_file", new { path = "A.cs", old_text = "same", new_text = "new", expected_sha256 = Hash(read) });
        Assert.True(result.IsError);
        Assert.Equal("same same", await File.ReadAllTextAsync(FilePath("A.cs")));
    }

    [Fact]
    public async Task IgnoreRulesAffectListingSearchReadAndWrite()
    {
        await File.WriteAllTextAsync(FilePath(".gitignore"), "hidden/\n*.generated.cs\n");
        await File.WriteAllTextAsync(FilePath(".slcopilotignore"), "private/\n");
        Directory.CreateDirectory(FilePath("hidden"));
        Directory.CreateDirectory(FilePath("private"));
        await File.WriteAllTextAsync(FilePath("hidden/A.cs"), "needle");
        await File.WriteAllTextAsync(FilePath("private/A.cs"), "needle");
        await File.WriteAllTextAsync(FilePath("A.generated.cs"), "needle");
        await File.WriteAllTextAsync(FilePath("Public.cs"), "needle");
        var workspace = Workspace();
        var list = await Call(workspace, "list_directory", new { path = "." });
        Assert.Contains("Public.cs", list.Content);
        Assert.DoesNotContain("hidden", list.Content);
        var search = await Call(workspace, "search_files", new { path = ".", query = "needle" });
        Assert.Contains("Public.cs", search.Content);
        Assert.DoesNotContain("hidden/A.cs", search.Content);
        Assert.DoesNotContain("private/A.cs", search.Content);
        Assert.True((await Call(workspace, "read_file", new { path = "hidden/A.cs" })).IsError);
        Assert.True((await Call(workspace, "write_file", new { path = "private/New.cs", content = "x", expected_sha256 = "new" })).IsError);
    }

    [Fact]
    public async Task BinaryInvalidUtf8AndOversizedFilesAreRejected()
    {
        await File.WriteAllBytesAsync(FilePath("Binary.cs"), [65, 0, 66]);
        await File.WriteAllBytesAsync(FilePath("Invalid.cs"), [255, 254, 255]);
        await File.WriteAllBytesAsync(FilePath("Large.cs"), new byte[131073]);
        foreach (var file in new[] { "Binary.cs", "Invalid.cs", "Large.cs" })
            Assert.True((await Call(Workspace(), "read_file", new { path = file })).IsError);
    }

    [Fact]
    public async Task HardLinksCannotReadOrOverwriteOutsideFiles()
    {
        var outside = Path.Combine(temp, "outside.cs");
        await File.WriteAllTextAsync(outside, "outside");
        Assert.True(CreateHardLink(FilePath("linked.cs"), outside, IntPtr.Zero));
        var workspace = Workspace();
        Assert.True((await Call(workspace, "read_file", new { path = "linked.cs" })).IsError);
        Assert.True((await Call(workspace, "write_file", new { path = "linked.cs", content = "oops", expected_sha256 = "new" })).IsError);
        Assert.Equal("outside", await File.ReadAllTextAsync(outside));
    }

    [Fact]
    public async Task DirectoryJunctionCannotEscapeWorkspace()
    {
        var outside = Path.Combine(temp, "external");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "A.cs"), "outside");
        var link = FilePath("junction");
        var info = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(info)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        try
        {
            var workspace = Workspace();
            Assert.True((await Call(workspace, "read_file", new { path = "junction/A.cs" })).IsError);
            Assert.True((await Call(workspace, "write_file", new { path = "junction/New.cs", content = "x", expected_sha256 = "new" })).IsError);
            Assert.False(File.Exists(Path.Combine(outside, "New.cs")));
            Assert.Throws<WorkspaceException>(() => new WorkspacePolicy(link));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task BackupFailureDoesNotCreateDestinationFile()
    {
        await File.WriteAllTextAsync(Backups, "blocking file");
        var workspace = Workspace();
        Assert.True((await Call(workspace, "write_file", new { path = "New.cs", content = "x", expected_sha256 = "new" })).IsError);
        Assert.False(File.Exists(FilePath("New.cs")));
    }

    [Fact]
    public async Task ChangeBetweenReadAndWriteGuardIsNotOverwritten()
    {
        await File.WriteAllTextAsync(FilePath("A.cs"), "before");
        var workspace = Workspace(guard: (path, ct) => File.WriteAllTextAsync(path, "concurrent edit", ct));
        var read = await Call(workspace, "read_file", new { path = "A.cs" });
        var result = await Call(workspace, "write_file", new { path = "A.cs", content = "agent", expected_sha256 = Hash(read) });
        Assert.True(result.IsError);
        Assert.Equal("concurrent edit", await File.ReadAllTextAsync(FilePath("A.cs")));
    }

    [Fact]
    public async Task DoubleStarIgnoreMatchesRootAndNestedDirectories()
    {
        await File.WriteAllTextAsync(FilePath(".slcopilotignore"), "**/private-data/**\n");
        var workspace = Workspace();
        foreach (var path in new[] { "private-data/file.cs", "src/private-data/file.cs" })
            Assert.True((await Call(workspace, "write_file", new { path, content = "x", expected_sha256 = "new" })).IsError);
    }

    [Fact]
    public async Task FailedMissingFileReadDoesNotLeakAbsoluteHostPath()
    {
        var result = await Call(Workspace(), "read_file", new { path = "Missing.cs" });
        Assert.True(result.IsError);
        Assert.DoesNotContain(Root, result.Content);
    }

    [Fact]
    public async Task CorruptBackupPreventsUndoBeforeAnyFilesAreRestored()
    {
        var workspace = Workspace();
        await Call(workspace, "write_file", new { path = "A.cs", content = "a", expected_sha256 = "new" });
        await Call(workspace, "write_file", new { path = "B.cs", content = "b", expected_sha256 = "new" });
        var backup = Directory.GetFiles(workspace.BackupDirectory, "*.before").Last();
        await File.WriteAllTextAsync(backup, "corrupted");
        await Assert.ThrowsAsync<WorkspaceException>(() => workspace.UndoAsync(CancellationToken.None));
        Assert.True(File.Exists(FilePath("A.cs")));
        Assert.True(File.Exists(FilePath("B.cs")));
    }

    [Fact]
    public async Task CancelBeforeWriteLeavesNoFile()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Workspace().ExecuteAsync("write_file",
            Args(new { path = "A.cs", content = "x", expected_sha256 = "new" }), cts.Token));
        Assert.False(File.Exists(FilePath("A.cs")));
    }

    [Fact]
    public void BackupsInsideWorkspaceAndBroadRootsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new WorkspaceTools(Root, true, Path.Combine(Root, "backups")));
        Assert.Throws<ArgumentException>(() => new WorkspacePolicy(Path.GetPathRoot(Root)!));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    public void Dispose()
    {
        // Only remove this fixture's GUID-named directory under the OS temp folder.
        if (Path.GetFullPath(temp).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            Directory.Delete(temp, recursive: true);
    }
}

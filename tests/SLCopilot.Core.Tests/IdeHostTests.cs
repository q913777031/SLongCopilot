using System.Text;
using System.Text.Json;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class IdeHostTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "SLCopilot-ide-tests-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(temp, "project");
    public IdeHostTests() => Directory.CreateDirectory(Root);
    public void Dispose() { try { Directory.Delete(temp, true); } catch (IOException) { } }

    private WorkspaceTools Workspace(bool writes, IIdeHost? host) => new(Root, writes, Path.Combine(temp, "backups"), ide: host);
    private static Task<ToolResult> Call(WorkspaceTools tools, string name, object? args = null) =>
        tools.ExecuteAsync(name, JsonSerializer.SerializeToElement(args ?? new { }), CancellationToken.None);

    [Fact]
    public async Task BuildIsRefusedInAskModeWithoutAskingTheUser()
    {
        var host = new FakeHost();
        var result = await Call(Workspace(false, host), "build_solution");
        Assert.True(result.IsError);
        Assert.Contains("Agent mode", result.Content);
        Assert.Equal(0, host.Builds);
    }

    [Fact]
    public async Task BuildReportsOutcomeAndMapsDiagnosticPaths()
    {
        var host = new FakeHost
        {
            Diagnostics =
            [
                new("error", Path.Combine(Root, "src", "A.cs"), 3, 5, "App", "CS1002: ; expected"),
                new("warning", Path.Combine(temp, "outside", "Secret.cs"), 1, 1, "Other", "unused"),
            ],
        };
        var result = await Call(Workspace(true, host), "build_solution");
        Assert.False(result.IsError, result.Content);
        Assert.Equal(1, host.Builds);
        var json = JsonDocument.Parse(result.Content).RootElement;
        Assert.False(json.GetProperty("succeeded").GetBoolean());
        var items = json.GetProperty("diagnostics").EnumerateArray().ToArray();
        Assert.Equal("src/A.cs", items[0].GetProperty("file").GetString());
        Assert.Equal(3, items[0].GetProperty("line").GetInt32());
        Assert.Equal("Secret.cs", items[1].GetProperty("file").GetString());
        Assert.DoesNotContain("outside", result.Content);
    }

    [Fact]
    public async Task DeclinedBuildIsReportedToTheModel()
    {
        var host = new FakeHost { DeclineBuild = true };
        var result = await Call(Workspace(true, host), "build_solution");
        Assert.True(result.IsError);
        Assert.Contains("declined", result.Content);
    }

    [Fact]
    public async Task ErrorsAreReadableInAskModeAndToolsNeedAHost()
    {
        var host = new FakeHost { Diagnostics = [new("error", "", 0, 0, "", "Solution-level error")] };
        var result = await Call(Workspace(false, host), "get_errors");
        Assert.False(result.IsError, result.Content);
        Assert.Contains("Solution-level error", result.Content);
        Assert.True((await Call(Workspace(true, null), "get_errors")).IsError);
        Assert.True((await Call(Workspace(true, null), "build_solution")).IsError);
    }

    [Fact]
    public async Task SymbolResultsAreRelativeAndRespectIgnoreRules()
    {
        System.IO.File.WriteAllText(Path.Combine(Root, ".slcopilotignore"), "Generated/\n");
        var host = new FakeHost
        {
            Symbols =
            [
                new("method", "OrderService.Save", Path.Combine(Root, "src", "OrderService.cs"), 12, "public void Save()"),
                new("method", "OrderService.Save", Path.Combine(Root, "Generated", "Proxy.cs"), 3, "secret"),
                new("method", "OrderService.Save", Path.Combine(temp, "elsewhere", "Lib.cs"), 7, "lib"),
            ],
        };
        var result = await Call(Workspace(false, host), "find_references", new { name = " OrderService.Save " });
        Assert.False(result.IsError, result.Content);
        Assert.Equal("OrderService.Save", host.LastQuery);
        Assert.Contains("\"src/OrderService.cs\"", result.Content);
        Assert.Contains("\"Lib.cs\"", result.Content);
        Assert.DoesNotContain("Proxy.cs", result.Content);
        Assert.DoesNotContain("elsewhere", result.Content);
        Assert.True((await Call(Workspace(false, null), "find_symbol", new { name = "X" })).IsError);
    }

    // 以下测试实际读写文件，依赖 Windows 的文件身份检查（FileSafety），只在 Windows 上运行。

    [Fact]
    public async Task OpenDocumentsAreWrittenThroughTheEditorAndUndoneThroughIt()
    {
        var file = Path.Combine(Root, "A.cs");
        System.IO.File.WriteAllText(file, "class A {}\r\n");
        var host = new FakeHost { OpenFile = file };
        var workspace = Workspace(true, host);
        var read = await Call(workspace, "read_file", new { path = "A.cs" });
        var hash = JsonDocument.Parse(read.Content).RootElement.GetProperty("sha256").GetString();
        var write = await Call(workspace, "edit_file", new { path = "A.cs", old_text = "class A", new_text = "class B", expected_sha256 = hash });
        Assert.False(write.IsError, write.Content);
        Assert.Contains("\"via\":\"editor\"", write.Content);
        Assert.Equal("A.cs\n@@ -1 +1 @@\n-class A {}\n+class B {}", write.Display);
        Assert.Equal("class B {}\r\n", System.IO.File.ReadAllText(file));
        // 经编辑器写入后再次修改：版本与已知内容都已更新，可直接继续编辑。
        var again = await Call(workspace, "edit_file", new { path = "A.cs", old_text = "class B", new_text = "class C" });
        Assert.False(again.IsError, again.Content);
        await Call(workspace, "edit_file", new { path = "A.cs", old_text = "class C", new_text = "class B" });
        Assert.Equal(1, workspace.ChangeCount);
        await workspace.UndoAsync(CancellationToken.None);
        Assert.Equal("class A {}\r\n", System.IO.File.ReadAllText(file));
        Assert.Equal(4, host.EditorWrites);
    }

    [Fact]
    public async Task EditorRejectionLeavesNoChangeRecord()
    {
        var file = Path.Combine(Root, "A.cs");
        System.IO.File.WriteAllText(file, "class A {}\r\n");
        var host = new FakeHost { OpenFile = file, RejectEditorWrite = true };
        var workspace = Workspace(true, host);
        var read = await Call(workspace, "read_file", new { path = "A.cs" });
        var hash = JsonDocument.Parse(read.Content).RootElement.GetProperty("sha256").GetString();
        var write = await Call(workspace, "write_file", new { path = "A.cs", content = "class B {}\r\n", expected_sha256 = hash });
        Assert.True(write.IsError);
        Assert.Equal(0, workspace.ChangeCount);
        Assert.Equal("class A {}\r\n", System.IO.File.ReadAllText(file));
    }

    [Fact]
    public async Task UnsavedEditorTextIsReturnedAndFlagged()
    {
        var file = Path.Combine(Root, "A.cs");
        System.IO.File.WriteAllText(file, "class A {}\r\n");
        var host = new FakeHost { OpenFile = file, Unsaved = "class A { int x; }\r\n" };
        var read = await Call(Workspace(false, host), "read_file", new { path = "A.cs" });
        Assert.False(read.IsError, read.Content);
        var json = JsonDocument.Parse(read.Content).RootElement;
        Assert.True(json.GetProperty("unsaved_editor_changes").GetBoolean());
        Assert.Equal("1\tclass A { int x; }", json.GetProperty("content").GetString());
    }

    [Fact]
    public async Task ApprovedUserEditsAreWrittenAndReportedToTheModel()
    {
        var file = Path.Combine(Root, "A.cs");
        System.IO.File.WriteAllText(file, "class A {}\r\n");
        WriteProposal? seen = null;
        var workspace = new WorkspaceTools(Root, true, Path.Combine(temp, "backups"), confirmWrite: (proposal, _) =>
        {
            seen = proposal;
            proposal.Revised = "class A { /* 用户保留的部分 */ }\r\n";
            return Task.FromResult(true);
        });
        var read = await Call(workspace, "read_file", new { path = "A.cs" });
        var hash = JsonDocument.Parse(read.Content).RootElement.GetProperty("sha256").GetString();
        var write = await Call(workspace, "write_file", new { path = "A.cs", content = "class B {}\r\n", expected_sha256 = hash });
        Assert.False(write.IsError, write.Content);
        Assert.Equal("class A {}\r\n", seen!.Before);
        Assert.Equal("class B {}\r\n", seen.After);
        Assert.Equal("class A { /* 用户保留的部分 */ }\r\n", System.IO.File.ReadAllText(file));
        Assert.True(JsonDocument.Parse(write.Content).RootElement.GetProperty("user_edited").GetBoolean());
        Assert.Contains("用户保留的部分", workspace.DescribeDiffs());
    }

    [Fact]
    public async Task HierarchyAndProjectGraphHideHostPathsAndKeepFrameworkSymbols()
    {
        var host = new FakeHost();
        host.Hierarchy.Add(new("base", "Shop.ShapeBase", Path.Combine(Root, "src", "ShapeBase.cs"), 3, "Shop.Circle"));
        host.Hierarchy.Add(new("interface", "System.IDisposable", "", 0, "Shop.Circle"));
        host.Hierarchy.Add(new("derived", "Other.Thing", Path.Combine(temp, "outside", "Thing.cs"), 7, "Shop.Circle"));
        host.Projects.Add(new("Shop.Core", "C#", Path.Combine(Root, "src", "Shop.Core.csproj"), 12, []));
        host.Projects.Add(new("Shop.App", "C#", Path.Combine(temp, "elsewhere", "Shop.App.csproj"), 30, ["Shop.Core"]));
        var hierarchy = JsonDocument.Parse((await Call(Workspace(false, host), "get_hierarchy", new { name = "Circle" })).Content).RootElement.GetProperty("results");
        Assert.Equal(new[] { "src/ShapeBase.cs", "", "Thing.cs" }, hierarchy.EnumerateArray().Select(r => r.GetProperty("file").GetString()));
        Assert.Equal("Shop.Circle", hierarchy[1].GetProperty("text").GetString());
        var graph = JsonDocument.Parse((await Call(Workspace(false, host), "get_project_graph")).Content).RootElement.GetProperty("projects");
        Assert.Equal("src/Shop.Core.csproj", graph[0].GetProperty("file").GetString());
        Assert.Equal("Shop.App.csproj", graph[1].GetProperty("file").GetString());
        Assert.Equal("Shop.Core", graph[1].GetProperty("references")[0].GetString());
        Assert.True((await Call(Workspace(false, null), "get_hierarchy", new { name = "X" })).IsError);
        Assert.True((await Call(Workspace(false, null), "get_project_graph")).IsError);
    }

    private sealed class FakeHost : IIdeHost
    {
        public IdeDiagnostic[] Diagnostics { get; init; } = [];
        public bool DeclineBuild { get; init; }
        public string? OpenFile { get; init; }
        public string? Unsaved { get; init; }
        public bool RejectEditorWrite { get; init; }
        public int Builds { get; private set; }
        public int EditorWrites { get; private set; }

        public Task<string?> GetUnsavedTextAsync(string fullPath, CancellationToken cancellationToken) =>
            Task.FromResult(IsOpen(fullPath) ? Unsaved : null);

        public Task<bool> TryWriteOpenDocumentAsync(string fullPath, string expectedText, string newText, TextFormat format, CancellationToken cancellationToken)
        {
            if (!IsOpen(fullPath)) return Task.FromResult(false);
            if (RejectEditorWrite) throw new WorkspaceException("Target is open with unsaved changes.");
            Assert.Equal(expectedText, System.IO.File.ReadAllText(fullPath));
            System.IO.File.WriteAllBytes(fullPath, TextCodec.Encode(newText, format));
            EditorWrites++;
            return Task.FromResult(true);
        }

        public Task<BuildOutcome> BuildSolutionAsync(string? projectPath, CancellationToken cancellationToken)
        {
            if (DeclineBuild) throw new WorkspaceException("The user declined the build.");
            Builds++;
            return Task.FromResult(new BuildOutcome(Diagnostics.All(d => d.Severity != "error"), false));
        }

        public Task<IReadOnlyList<IdeDiagnostic>> GetDiagnosticsAsync(int maxItems, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IdeDiagnostic>>(Diagnostics.Take(maxItems).ToList());

        public CodeSymbol[] Symbols { get; init; } = [];
        public string? LastQuery { get; private set; }

        public Task<IReadOnlyList<CodeSymbol>> FindSymbolsAsync(string name, int maxItems, CancellationToken cancellationToken)
        {
            LastQuery = name;
            return Task.FromResult<IReadOnlyList<CodeSymbol>>(Symbols.Take(maxItems).ToList());
        }

        public Task<IReadOnlyList<CodeSymbol>> FindReferencesAsync(string name, int maxItems, CancellationToken cancellationToken)
        {
            LastQuery = name;
            return Task.FromResult<IReadOnlyList<CodeSymbol>>(Symbols.Take(maxItems).ToList());
        }

        public Task<IReadOnlyList<CodeSymbol>> GetOutlineAsync(string fullPath, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CodeSymbol>>(Symbols.Where(s => s.File == fullPath).ToList());

        public List<CodeSymbol> Hierarchy { get; } = [];
        public List<ProjectNode> Projects { get; } = [];

        public Task<IReadOnlyList<CodeSymbol>> GetHierarchyAsync(string name, int maxItems, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CodeSymbol>>(Hierarchy.Take(maxItems).ToList());

        public Task<IReadOnlyList<ProjectNode>> GetProjectGraphAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProjectNode>>(Projects);
        public Task<IReadOnlyList<SymbolSource>> ReadSymbolAsync(string name, int maxLines, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SymbolSource>>([]);
        public Task<IReadOnlyList<IdeDiagnostic>?> CheckFileAsync(string fullPath, string? text, int maxItems, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IdeDiagnostic>?>(null);
        public Task<IReadOnlyList<CodeSymbol>> GetCallersAsync(string name, int maxItems, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CodeSymbol>>([]);
        public Task<IReadOnlyList<CodeSymbol>> GetCalleesAsync(string name, int maxItems, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CodeSymbol>>([]);
        public Task<IReadOnlyList<CodeSymbol>> GetTypeDependenciesAsync(string name, int maxItems, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CodeSymbol>>([]);

        private bool IsOpen(string fullPath) => string.Equals(fullPath, OpenFile, StringComparison.OrdinalIgnoreCase);
    }
}

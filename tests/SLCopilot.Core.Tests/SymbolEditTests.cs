using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using SLCopilot;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

/// <summary>replace_symbol / insert_near_symbol 端到端：Roslyn 定位（AdhocWorkspace，内容与磁盘一致）+ WorkspaceTools 写入。</summary>
public sealed class SymbolEditTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "SLCopilot-symbol-edit-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(temp, "project");
    public SymbolEditTests() => Directory.CreateDirectory(Root);
    public void Dispose() { try { Directory.Delete(temp, true); } catch (IOException) { } }

    /// <summary>把文件写到磁盘并加入 Roslyn 工作区；<paramref name="targets"/> 大于 1 时模拟多目标框架（同一文件属于多个项目）。</summary>
    private (WorkspaceTools Tools, string Path) Setup(string source, int targets = 1, bool vb = false)
    {
        var path = Path.Combine(Root, vb ? "Shop.vb" : "Shop.cs");
        System.IO.File.WriteAllText(path, source);
        var workspace = new AdhocWorkspace();
        for (var t = 0; t < targets; t++)
        {
            var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "Shop" + t, "Shop", vb ? LanguageNames.VisualBasic : LanguageNames.CSharp,
                compilationOptions: vb ? new Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary) : null,
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));
            workspace.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(project.Id), Path.GetFileName(path),
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Create())), filePath: path));
        }
        return (new WorkspaceTools(Root, true, Path.Combine(temp, "backups"), ide: new RoslynHost(workspace.CurrentSolution)), path);
    }

    private static Task<ToolResult> Call(WorkspaceTools tools, string name, object args)
        => tools.ExecuteAsync(name, JsonSerializer.SerializeToElement(args), CancellationToken.None);

    [Fact]
    public async Task ReplaceKeepsRegionsAndNeighbours()
    {
        var (tools, path) = Setup("class Shop\n{\n    #region Orders\n    /// <summary>Old.</summary>\n    public int Save(int id) { return id; }\n    #endregion\n    public void Other() { }\n}\n");
        var result = await Call(tools, "replace_symbol", new { name = "Shop.Save", new_text = "    /// <summary>New.</summary>\n    public int Save(int id) => id + 1;" });
        Assert.False(result.IsError, result.Content);
        Assert.Equal("class Shop\n{\n    #region Orders\n    /// <summary>New.</summary>\n    public int Save(int id) => id + 1;\n    #endregion\n    public void Other() { }\n}\n",
            System.IO.File.ReadAllText(path));
    }

    [Fact]
    public async Task InsertAfterAddsWholeLines()
    {
        var (tools, path) = Setup("class Shop\n{\n    public void A() { }\n}\n");
        var result = await Call(tools, "insert_near_symbol", new { name = "Shop.A", text = "\n    public void B() { }" });
        Assert.False(result.IsError, result.Content);
        Assert.Equal("class Shop\n{\n    public void A() { }\n\n    public void B() { }\n}\n", System.IO.File.ReadAllText(path));
    }

    [Theory]
    [InlineData("class Shop\n{\n    private int a, b;\n}\n", "Shop.a")]
    [InlineData("enum Color { Red, Green }\n", "Color.Red")]
    [InlineData("record P(int X, int Y);\n", "P.X")]
    public async Task DeclarationsSharingALineAreRefused(string source, string name)
    {
        var (tools, path) = Setup(source);
        var result = await Call(tools, "replace_symbol", new { name, new_text = "" });
        Assert.True(result.IsError);
        Assert.Contains("shares its line", result.Content);
        Assert.Equal(source, System.IO.File.ReadAllText(path));
    }

    [Fact]
    public async Task MultiTargetDuplicatesCountAsOneDeclaration()
    {
        var (tools, path) = Setup("class Shop\n{\n    public void A() { }\n}\n", targets: 2);
        var result = await Call(tools, "replace_symbol", new { name = "Shop.A", new_text = "    public void A() { Run(); }" });
        Assert.False(result.IsError, result.Content);
        Assert.Contains("Run();", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public async Task SymbolEditOnAnUnreadFileKeepsWriteFileBlocked()
    {
        var (tools, _) = Setup("class Shop\n{\n    public void A() { }\n    public void B() { }\n}\n");
        var result = await Call(tools, "replace_symbol", new { name = "Shop.A", new_text = "    public void A() { }" + " // x" });
        Assert.False(result.IsError, result.Content);
        var hash = JsonDocument.Parse(result.Content).RootElement.GetProperty("sha256").GetString();
        var overwrite = await Call(tools, "write_file", new { path = "Shop.cs", content = "class Shop { }\n", expected_sha256 = hash });
        Assert.True(overwrite.IsError);
        Assert.Contains("only part of this file", overwrite.Content);
    }

    [Fact]
    public async Task ReplaceBetweenWithinLimitsTheSearchToOneMember()
    {
        var (tools, path) = Setup("class Shop\n{\n    void A()\n    {\n        try { }\n        finally { }\n    }\n    void B()\n    {\n        try { }\n        finally { }\n    }\n}\n");
        await Call(tools, "read_file", new { path = "Shop.cs" });
        var global = await Call(tools, "replace_between", new { path = "Shop.cs", start_line = "try { }", end_line = "finally { }", new_text = "        Run();" });
        Assert.True(global.IsError);
        var scoped = await Call(tools, "replace_between", new { path = "Shop.cs", start_line = "try { }", end_line = "finally { }", new_text = "        Run();", within = "Shop.B" });
        Assert.False(scoped.IsError, scoped.Content);
        Assert.Equal("class Shop\n{\n    void A()\n    {\n        try { }\n        finally { }\n    }\n    void B()\n    {\n        Run();\n    }\n}\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public async Task VbMethodsSpanTheirBlockAndAutoPropertiesStayAlone()
    {
        const string source = "Public Class Customer\n    Public Property Name As String\n    Public Sub Save()\n        Run()\n    End Sub\n    Private Sub Run()\n    End Sub\nEnd Class\n";
        var (tools, path) = Setup(source, vb: true);
        var method = await Call(tools, "replace_symbol", new { name = "Customer.Save", new_text = "    Public Sub Save()\n    End Sub" });
        Assert.False(method.IsError, method.Content);
        Assert.Equal("Public Class Customer\n    Public Property Name As String\n    Public Sub Save()\n    End Sub\n    Private Sub Run()\n    End Sub\nEnd Class\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public async Task VbAutoPropertyReplaceTouchesOnlyItsLine()
    {
        const string source = "Public Class Customer\n    Public Property Name As String\n    Public Sub Save()\n    End Sub\nEnd Class\n";
        var (tools, path) = Setup(source, vb: true);
        var property = await Call(tools, "replace_symbol", new { name = "Customer.Name", new_text = "" });
        Assert.False(property.IsError, property.Content);
        Assert.Equal("Public Class Customer\n    Public Sub Save()\n    End Sub\nEnd Class\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public async Task VbQualifiedNamesWorkForFindSymbolAndReferences()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "V", "V", LanguageNames.VisualBasic,
            compilationOptions: new Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));
        const string source = "Public Class Customer\n    Public Sub Save(id As Integer)\n    End Sub\n    Sub Run()\n        Save(1)\n    End Sub\nEnd Class\n";
        workspace.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(project.Id), "C.vb",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Create())), filePath: Path.Combine(Root, "C.vb")));
        var found = await RoslynNavigator.FindSymbolsAsync(workspace.CurrentSolution, "Customer.Save", 10, CancellationToken.None);
        Assert.Equal("Customer.Save(Integer)", Assert.Single(found).Name);
        var references = await RoslynNavigator.FindReferencesAsync(workspace.CurrentSolution, "Customer.Save", 10, CancellationToken.None);
        Assert.Single(references);
    }

    [Fact]
    public async Task ReplaceBetweenKeepsTheFinalNewline()
    {
        var (tools, path) = Setup("class Shop\n{\n}\n");
        await Call(tools, "read_file", new { path = "Shop.cs" });
        var result = await Call(tools, "replace_between", new { path = "Shop.cs", start_line = "{", end_line = "}", new_text = "{ }" });
        Assert.False(result.IsError, result.Content);
        Assert.Equal("class Shop\n{ }\n", System.IO.File.ReadAllText(path));
    }

    private sealed class RoslynHost(Solution solution) : IIdeHost
    {
        public Task<IReadOnlyList<SymbolSource>> ReadSymbolAsync(string name, int maxLines, CancellationToken cancellationToken) =>
            RoslynNavigator.ReadSymbolAsync(solution, name, maxLines, cancellationToken);
        public Task<string?> GetUnsavedTextAsync(string fullPath, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<bool> TryWriteOpenDocumentAsync(string fullPath, string expectedText, string newText, TextFormat format, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<BuildOutcome> BuildSolutionAsync(string? projectPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<IdeDiagnostic>> GetDiagnosticsAsync(int maxItems, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IdeDiagnostic>>([]);
        public Task<IReadOnlyList<CodeSymbol>> FindSymbolsAsync(string name, int maxItems, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CodeSymbol>>([]);
        public Task<IReadOnlyList<CodeSymbol>> FindReferencesAsync(string name, int maxItems, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CodeSymbol>>([]);
        public Task<IReadOnlyList<CodeSymbol>> GetOutlineAsync(string fullPath, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CodeSymbol>>([]);
        public Task<IReadOnlyList<CodeSymbol>> GetHierarchyAsync(string name, int maxItems, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CodeSymbol>>([]);
        public Task<IReadOnlyList<ProjectNode>> GetProjectGraphAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProjectNode>>([]);
        public Task<IReadOnlyList<IdeDiagnostic>?> CheckFileAsync(string fullPath, string? text, int maxItems, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IdeDiagnostic>?>(null);
        public Task<IReadOnlyList<CodeSymbol>> GetCallersAsync(string name, int maxItems, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CodeSymbol>>([]);
        public Task<IReadOnlyList<CodeSymbol>> GetCalleesAsync(string name, int maxItems, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CodeSymbol>>([]);
        public Task<IReadOnlyList<CodeSymbol>> GetTypeDependenciesAsync(string name, int maxItems, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CodeSymbol>>([]);
    }
}

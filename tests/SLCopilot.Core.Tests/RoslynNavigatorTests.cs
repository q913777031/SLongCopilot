using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using SLCopilot;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class RoslynNavigatorTests
{
    private const string OrderService = """
        namespace Shop
        {
            public class OrderService
            {
                private int count;
                public OrderService() { }
                public int Count => count;
                public void Save(int id)
                {
                    var local = id;
                    count++;
                }
                public class Nested { public void Save() { } }
            }
        }
        """;

    private const string Caller = """
        namespace Shop
        {
            class Caller
            {
                void Run(OrderService service)
                {
                    service.Save(1);
                    service.Save(2);
                }
            }
        }
        """;

    private static Solution CreateSolution(out string orderPath)
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "Shop", "Shop", LanguageNames.CSharp,
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));
        orderPath = Path.Combine(Path.GetTempPath(), "Shop", "OrderService.cs");
        workspace.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(project.Id), "OrderService.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(OrderService), VersionStamp.Create())), filePath: orderPath));
        workspace.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(project.Id), "Caller.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(Caller), VersionStamp.Create())), filePath: Path.Combine(Path.GetTempPath(), "Shop", "Caller.cs")));
        return workspace.CurrentSolution;
    }

    [Fact]
    public async Task QualifiedNamesNarrowDeclarations()
    {
        var solution = CreateSolution(out _);
        var all = await RoslynNavigator.FindSymbolsAsync(solution, "Save", 50, CancellationToken.None);
        Assert.Equal(2, all.Count);
        var qualified = await RoslynNavigator.FindSymbolsAsync(solution, "OrderService.Save", 50, CancellationToken.None);
        Assert.Equal("Shop.OrderService.Save(int)", Assert.Single(qualified).Name);
        Assert.Equal(8, qualified[0].Line);
        Assert.Equal("method", qualified[0].Kind);
    }

    [Fact]
    public async Task ReadSymbolIncludesAttachedCommentsButNotRegionsOrDetachedComments()
    {
        const string source = """
            class Regions
            {
                #region Members
                // detached note

                // attached note
                /// <summary>Doc.</summary>
                [System.Obsolete]
                public void A() { }
                #endregion
            }
            """;
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "R", "R", LanguageNames.CSharp,
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));
        workspace.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(project.Id), "Regions.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Create())), filePath: Path.Combine(Path.GetTempPath(), "R", "Regions.cs")));
        var symbol = Assert.Single(await RoslynNavigator.ReadSymbolAsync(workspace.CurrentSolution, "Regions.A", 100, CancellationToken.None));
        Assert.Equal(6, symbol.StartLine);
        Assert.Equal(9, symbol.EndLine);
        Assert.StartsWith("    // attached note", symbol.Text);
        Assert.EndsWith("public void A() { }", symbol.Text);
    }

    [Fact]
    public async Task ReadSymbolFiltersOverloadsByParameterList()
    {
        var solution = CreateSolution(out _);
        Assert.Single(await RoslynNavigator.ReadSymbolAsync(solution, "OrderService.Save(int)", 100, CancellationToken.None));
        Assert.Single(await RoslynNavigator.ReadSymbolAsync(solution, "OrderService.Save( int )", 100, CancellationToken.None));
        Assert.Empty(await RoslynNavigator.ReadSymbolAsync(solution, "OrderService.Save(string)", 100, CancellationToken.None));
    }

    [Fact]
    public async Task ReferencesIncludeLineText()
    {
        var solution = CreateSolution(out _);
        var references = await RoslynNavigator.FindReferencesAsync(solution, "OrderService.Save", 100, CancellationToken.None);
        Assert.Equal(["[Caller.Run(OrderService)] service.Save(1);", "[Caller.Run(OrderService)] service.Save(2);"], references.Select(r => r.Text));
        Assert.All(references, r => Assert.EndsWith("Caller.cs", r.File));
    }

    private const string Shapes = """
        namespace Geo
        {
            public interface IShape { double Area(); }
            public abstract class ShapeBase : IShape, System.IDisposable
            {
                public abstract double Area();
                public void Dispose() { }
            }
            public class Circle : ShapeBase { public override double Area() => 3.14; }
            public class Ring : Circle { }
        }
        """;

    private static Solution CreateShapes()
    {
        var workspace = new AdhocWorkspace();
        var references = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var geo = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "Geo", "Geo", LanguageNames.CSharp, metadataReferences: references));
        workspace.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(geo.Id), "Shapes.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(Shapes), VersionStamp.Create())), filePath: Path.Combine(Path.GetTempPath(), "Geo", "Shapes.cs")));
        workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "App", "App", LanguageNames.CSharp, metadataReferences: references,
            projectReferences: [new ProjectReference(geo.Id)]));
        return workspace.CurrentSolution;
    }

    [Fact]
    public async Task HierarchyShowsBasesInterfacesDerivedTypesAndOverrides()
    {
        var solution = CreateShapes();
        var circle = await RoslynNavigator.GetHierarchyAsync(solution, "Geo.Circle", 50, CancellationToken.None);
        Assert.Contains(circle, r => r.Kind == "base" && r.Name == "Geo.ShapeBase" && r.Line == 4);
        Assert.Contains(circle, r => r.Kind == "interface" && r.Name == "Geo.IShape");
        Assert.Contains(circle, r => r.Kind == "interface" && r.Name == "System.IDisposable" && r.File == "");
        Assert.Contains(circle, r => r.Kind == "derived" && r.Name == "Geo.Ring");
        Assert.All(circle, r => Assert.Equal("Geo.Circle", r.Text));

        var shape = await RoslynNavigator.GetHierarchyAsync(solution, "IShape", 50, CancellationToken.None);
        Assert.Equal(new[] { "Geo.Circle", "Geo.Ring", "Geo.ShapeBase" }, shape.Where(r => r.Kind == "implementation").Select(r => r.Name).OrderBy(n => n));

        var area = await RoslynNavigator.GetHierarchyAsync(solution, "ShapeBase.Area", 50, CancellationToken.None);
        Assert.Contains(area, r => r.Kind == "implements" && r.Name == "Geo.IShape.Area()");
        Assert.Contains(area, r => r.Kind == "overridden-by" && r.Name == "Geo.Circle.Area()");
        var circleArea = await RoslynNavigator.GetHierarchyAsync(solution, "Circle.Area", 50, CancellationToken.None);
        Assert.Contains(circleArea, r => r.Kind == "overrides" && r.Name == "Geo.ShapeBase.Area()");
        var interfaceArea = await RoslynNavigator.GetHierarchyAsync(solution, "IShape.Area", 50, CancellationToken.None);
        Assert.Contains(interfaceArea, r => r.Kind == "implemented-by" && r.Name == "Geo.ShapeBase.Area()");
    }

    [Fact]
    public void ProjectGraphListsProjectReferences()
    {
        var graph = RoslynNavigator.GetProjectGraph(CreateShapes());
        Assert.Equal(new[] { "App", "Geo" }, graph.Select(p => p.Name));
        Assert.Equal(new[] { "Geo" }, graph[0].References);
        Assert.Empty(graph[1].References);
        Assert.Equal(1, graph[1].Documents);
    }

    [Fact]
    public async Task OutlineListsMembersWithoutLocals()
    {
        var solution = CreateSolution(out var path);
        var outline = await RoslynNavigator.GetOutlineAsync(solution, path, CancellationToken.None);
        Assert.Equal(["OrderService", "count", ".ctor", "Count", "Save", "Nested", "Save"], outline.Select(s => s.Name));
        Assert.DoesNotContain(outline, s => s.Name == "local");
        Assert.Equal(0, outline[0].Depth);
        Assert.Equal(1, outline[1].Depth);
        Assert.Equal(2, outline.Last().Depth);
        Assert.Empty(await RoslynNavigator.GetOutlineAsync(solution, "/nowhere/X.cs", CancellationToken.None));
    }
}

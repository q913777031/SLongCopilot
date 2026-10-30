using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

/// <summary>分段读取与单文件上限。实际读写文件，需在 Windows 上运行（FileSafety）。</summary>
public sealed class RangedReadTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "SLCopilot-ranged-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(temp, "project");

    public RangedReadTests()
    {
        Directory.CreateDirectory(Root);
        System.IO.File.WriteAllText(Path.Combine(Root, "Small.cs"), string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i}\n")));
        System.IO.File.WriteAllText(Path.Combine(Root, "Big.cs"), string.Concat(Enumerable.Range(1, 3000).Select(i => $"// generated line {i:D5} ..........\n")));
    }

    public void Dispose() { try { Directory.Delete(temp, true); } catch (IOException) { } }

    private WorkspaceTools Tools(int maxFileBytes = WorkspaceTools.DefaultMaxFileBytes) => new(Root, true, Path.Combine(temp, "backups"), maxFileBytes: maxFileBytes);
    private static Task<ToolResult> Call(WorkspaceTools tools, string name, object args) => tools.ExecuteAsync(name, JsonSerializer.SerializeToElement(args), CancellationToken.None);

    [Fact]
    public async Task RangesReturnRequestedLinesWithWholeFileHash()
    {
        var tools = Tools();
        var whole = JsonDocument.Parse((await Call(tools, "read_file", new { path = "Small.cs" })).Content).RootElement;
        var part = await Call(tools, "read_file", new { path = "Small.cs", start_line = 3, line_count = 2 });
        Assert.False(part.IsError, part.Content);
        var json = JsonDocument.Parse(part.Content).RootElement;
        Assert.Equal("3\tline 3\n4\tline 4", json.GetProperty("content").GetString());
        Assert.Equal(10, json.GetProperty("total_lines").GetInt32());
        Assert.Equal(4, json.GetProperty("end_line").GetInt32());
        Assert.Equal(whole.GetProperty("sha256").GetString(), json.GetProperty("sha256").GetString());
        var tail = JsonDocument.Parse((await Call(tools, "read_file", new { path = "Small.cs", start_line = 9 })).Content).RootElement;
        Assert.Equal("9\tline 9\n10\tline 10", tail.GetProperty("content").GetString());
        Assert.True((await Call(tools, "read_file", new { path = "Small.cs", start_line = 0 })).IsError);
        Assert.True((await Call(tools, "read_file", new { path = "Small.cs", line_count = 6000 })).IsError);
    }

    [Fact]
    public async Task FilesOverTheLimitMustBeReadInPartsAndCannotBeWritten()
    {
        var tools = Tools(maxFileBytes: 16 * 1024);
        var whole = await Call(tools, "read_file", new { path = "Big.cs" });
        Assert.True(whole.IsError);
        Assert.Contains("start_line", whole.Content);
        var part = await Call(tools, "read_file", new { path = "Big.cs", start_line = 2999, line_count = 10 });
        Assert.False(part.IsError, part.Content);
        var json = JsonDocument.Parse(part.Content).RootElement;
        Assert.Equal(3000, json.GetProperty("total_lines").GetInt32());
        Assert.StartsWith("2999\t// generated line 02999", json.GetProperty("content").GetString());
        var write = await Call(tools, "write_file", new { path = "Big.cs", content = "x", expected_sha256 = json.GetProperty("sha256").GetString() });
        Assert.True(write.IsError);
        var bigRange = JsonDocument.Parse((await Call(tools, "read_file", new { path = "Big.cs", start_line = 1, line_count = 5000 })).Content).RootElement;
        Assert.True(bigRange.GetProperty("content").GetString()!.Length <= 16 * 1024);
        Assert.Contains("end_line + 1", bigRange.GetProperty("note").GetString());
    }

    [Fact]
    public async Task EditMatchesCrlfFilesWithLfTextAndKeepsCrlf()
    {
        System.IO.File.WriteAllText(Path.Combine(Root, "Crlf.cs"), "class A\r\n{\r\n    void M() { }\r\n}\r\n");
        var tools = Tools();
        var read = JsonDocument.Parse((await Call(tools, "read_file", new { path = "Crlf.cs" })).Content).RootElement;
        var edit = await Call(tools, "edit_file", new { path = "Crlf.cs", old_text = "{\n    void M() { }\n}",
            new_text = "{\n    void M() { }\n    void N() { }\n}", expected_sha256 = read.GetProperty("sha256").GetString() });
        Assert.False(edit.IsError, edit.Content);
        Assert.Equal("class A\r\n{\r\n    void M() { }\r\n    void N() { }\r\n}\r\n", System.IO.File.ReadAllText(Path.Combine(Root, "Crlf.cs")));
    }

    [Fact]
    public async Task WritesUnifyMixedLineEndingsToTheDominantStyle()
    {
        var path = Path.Combine(Root, "Mixed.cs");
        System.IO.File.WriteAllText(path, "a\r\nb\nc\r\nd\r\n");
        var tools = Tools();
        async Task<string> Hash() => JsonDocument.Parse((await Call(tools, "read_file", new { path = "Mixed.cs" })).Content).RootElement.GetProperty("sha256").GetString()!;
        var edit = await Call(tools, "edit_file", new { path = "Mixed.cs", old_text = "d", new_text = "d2", expected_sha256 = await Hash() });
        Assert.False(edit.IsError, edit.Content);
        Assert.Equal("a\r\nb\r\nc\r\nd2\r\n", System.IO.File.ReadAllText(path));
        var write = await Call(tools, "write_file", new { path = "Mixed.cs", content = "x\ny\n", expected_sha256 = await Hash() });
        Assert.False(write.IsError, write.Content);
        Assert.Equal("x\r\ny\r\n", System.IO.File.ReadAllText(path));
        Assert.True(WorkspaceTools.HasMixedLineEndings("a\r\nb\n"));
        Assert.False(WorkspaceTools.HasMixedLineEndings("a\r\nb\r\n"));
        Assert.False(WorkspaceTools.HasMixedLineEndings("a\nb\n"));
    }

    [Fact]
    public async Task LongFilesAreReadInPartsByDefault()
    {
        var tools = Tools();
        var first = JsonDocument.Parse((await Call(tools, "read_file", new { path = "Big.cs" })).Content).RootElement;
        Assert.Equal(3000, first.GetProperty("total_lines").GetInt32());
        Assert.Equal(WorkspaceTools.WholeReadMaxLines, first.GetProperty("end_line").GetInt32());
        Assert.Contains("save tokens", first.GetProperty("note").GetString());
        var write = await Call(tools, "write_file", new { path = "Big.cs", content = "x", expected_sha256 = first.GetProperty("sha256").GetString() });
        Assert.True(write.IsError);
        Assert.Contains("edit_file", write.Content);
        var edit = await Call(tools, "edit_file", new { path = "Big.cs", old_text = "// generated line 00007", new_text = "// edited line 00007",
            expected_sha256 = first.GetProperty("sha256").GetString() });
        Assert.False(edit.IsError, edit.Content);
        Assert.Equal(3000, System.IO.File.ReadAllLines(Path.Combine(Root, "Big.cs")).Length);
    }
}

public sealed class ToolSchemaTests
{
    [Fact]
    public void ReadFileHasOptionalIntegerRangeParameters()
    {
        var tools = (JsonArray)typeof(AgentProtocol).Assembly.GetType("SLCopilot.Core.ToolDefinitions")!
            .GetMethod("Create")!.Invoke(null, [true, null])!;
        var read = tools.OfType<JsonObject>().Single(t => (string?)t["name"] == "read_file")["input_schema"]!;
        Assert.Equal(["path"], read["required"]!.AsArray().Select(n => (string?)n));
        Assert.Equal("integer", (string?)read["properties"]!["start_line"]!["type"]);
        Assert.Equal("integer", (string?)read["properties"]!["line_count"]!["type"]);
    }
}

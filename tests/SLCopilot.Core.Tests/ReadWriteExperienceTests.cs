using System.Text.Json;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

/// <summary>0.7.0 读写体验：带行号读取、编辑匹配容错（行号前缀、弯引号、整体缩进）、读取后被改动时返回差异、写入的界面差异。</summary>
public sealed class ReadWriteExperienceTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "SLCopilot-read-write-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(temp, "project");
    public ReadWriteExperienceTests() => Directory.CreateDirectory(Root);
    public void Dispose() { try { Directory.Delete(temp, true); } catch (IOException) { } }

    private WorkspaceTools Workspace() => new(Root, true, Path.Combine(temp, "backups"));
    private static Task<ToolResult> Call(WorkspaceTools tools, string name, object args)
        => tools.ExecuteAsync(name, JsonSerializer.SerializeToElement(args), CancellationToken.None);
    private string FilePath(string name) => Path.Combine(Root, name);

    private async Task<JsonElement> Read(WorkspaceTools tools, string path)
    {
        var read = await Call(tools, "read_file", new { path });
        Assert.False(read.IsError, read.Content);
        return JsonDocument.Parse(read.Content).RootElement;
    }

    [Fact]
    public async Task ReadNumbersLinesAndEditAcceptsCopiedPrefixes()
    {
        await File.WriteAllTextAsync(FilePath("A.cs"), "class A\r\n{\r\n    int x;\r\n}\r\n");
        var tools = Workspace();
        var json = await Read(tools, "A.cs");
        Assert.Equal("1\tclass A\n2\t{\n3\t    int x;\n4\t}", json.GetProperty("content").GetString());
        Assert.Equal(4, json.GetProperty("total_lines").GetInt32());

        var edit = await Call(tools, "edit_file", new { path = "A.cs", old_text = "3\t    int x;", new_text = "3\t    int y;" });
        Assert.False(edit.IsError, edit.Content);
        Assert.Contains("line-number prefixes", edit.Content);
        Assert.Equal("class A\r\n{\r\n    int y;\r\n}\r\n", await File.ReadAllTextAsync(FilePath("A.cs")));
    }

    [Fact]
    public async Task WriteFileRejectsNumberedContent()
    {
        var tools = Workspace();
        var result = await Call(tools, "write_file", new { path = "B.cs", content = "1\tclass B\n2\t{\n3\t}\n", expected_sha256 = "new" });
        Assert.True(result.IsError);
        Assert.Contains("line-number", result.Content);
        Assert.False(File.Exists(FilePath("B.cs")));
    }

    [Fact]
    public async Task CurlyQuotesMatchStraightQuotes()
    {
        await File.WriteAllTextAsync(FilePath("Q.cs"), "var s = \"hello\";\n");
        var tools = Workspace();
        await Read(tools, "Q.cs");
        var edit = await Call(tools, "edit_file", new { path = "Q.cs", old_text = "var s = “hello”;", new_text = "var s = \"world\";" });
        Assert.False(edit.IsError, edit.Content);
        Assert.Equal("var s = \"world\";\n", await File.ReadAllTextAsync(FilePath("Q.cs")));
    }

    [Fact]
    public async Task UniformIndentationDifferenceIsAdjusted()
    {
        await File.WriteAllTextAsync(FilePath("I.cs"), "class C\n{\n        void M()\n        {\n            Run();\n        }\n}\n");
        var tools = Workspace();
        await Read(tools, "I.cs");
        var edit = await Call(tools, "edit_file", new
        {
            path = "I.cs",
            old_text = "void M()\n{\n    Run();\n}",
            new_text = "void M()\n{\n    Run();\n    Stop();\n}",
        });
        Assert.False(edit.IsError, edit.Content);
        Assert.Contains("indentation", edit.Content);
        Assert.Equal("class C\n{\n        void M()\n        {\n            Run();\n            Stop();\n        }\n}\n", await File.ReadAllTextAsync(FilePath("I.cs")));
    }

    [Fact]
    public async Task AmbiguousIndentationMatchIsRejected()
    {
        await File.WriteAllTextAsync(FilePath("D.cs"), "    a();\n        a();\n");
        var tools = Workspace();
        await Read(tools, "D.cs");
        var edit = await Call(tools, "edit_file", new { path = "D.cs", old_text = "  a();", new_text = "  b();" });
        Assert.True(edit.IsError);
        Assert.Equal("    a();\n        a();\n", await File.ReadAllTextAsync(FilePath("D.cs")));
    }

    [Fact]
    public async Task ExternalChangeReturnsDiffAndRegistersTheNewVersion()
    {
        await File.WriteAllTextAsync(FilePath("E.cs"), "one\ntwo\nthree\n");
        var tools = Workspace();
        var first = await Read(tools, "E.cs");
        await File.WriteAllTextAsync(FilePath("E.cs"), "one\nTWO\nthree\n");

        var stale = await Call(tools, "edit_file", new { path = "E.cs", old_text = "three", new_text = "3", expected_sha256 = first.GetProperty("sha256").GetString() });
        Assert.True(stale.IsError);
        Assert.Contains("-two", stale.Content);
        Assert.Contains("+TWO", stale.Content);
        Assert.Equal("one\nTWO\nthree\n", await File.ReadAllTextAsync(FilePath("E.cs")));

        var retry = await Call(tools, "edit_file", new { path = "E.cs", old_text = "three", new_text = "3" });
        Assert.False(retry.IsError, retry.Content);
        Assert.Equal("one\nTWO\n3\n", await File.ReadAllTextAsync(FilePath("E.cs")));
    }

    [Fact]
    public async Task WritesCarryADisplayDiffThatIsNotSentToTheModel()
    {
        await File.WriteAllTextAsync(FilePath("F.cs"), "a\nb\nc\n");
        var tools = Workspace();
        await Read(tools, "F.cs");
        var edit = await Call(tools, "edit_file", new { path = "F.cs", old_text = "b", new_text = "B" });
        Assert.False(edit.IsError, edit.Content);
        Assert.Equal("F.cs\n@@ -1 +1 @@\n a\n-b\n+B\n c", edit.Display);
        Assert.DoesNotContain("@@", edit.Content);
        var read = await Call(tools, "read_file", new { path = "F.cs" });
        Assert.Null(read.Display);
    }

    [Fact]
    public void UnifiedDiffShowsHunksWithLineNumbersAndTruncates()
    {
        var before = string.Join("\n", Enumerable.Range(1, 20).Select(i => "l" + i));
        var after = before.Replace("l3", "x3").Replace("l18", "x18");
        var (text, truncated) = ChangePreview.UnifiedDiff(before, after, context: 1);
        Assert.False(truncated);
        Assert.Equal("@@ -2 +2 @@\n l2\n-l3\n+x3\n l4\n@@ -17 +17 @@\n l17\n-l18\n+x18\n l19", text);
        Assert.Equal(("", false), ChangePreview.UnifiedDiff("same\n", "same\r\n"));
        var (cut, isCut) = ChangePreview.UnifiedDiff("", string.Join("\n", Enumerable.Range(1, 100).Select(i => "n" + i)), maxLines: 10);
        Assert.True(isCut);
        Assert.EndsWith("... 90 more changed lines", cut);
    }

    [Fact]
    public async Task VeryLongLinesAreShortenedAndBlockWholeFileWrites()
    {
        await File.WriteAllTextAsync(FilePath("min.js"), "var a=1;\n" + new string('x', 5000) + "\n");
        var tools = Workspace();
        var json = await Read(tools, "min.js");
        var content = json.GetProperty("content").GetString()!;
        Assert.Contains("line shortened, 5,000 characters", content);
        Assert.True(content.Length < 2200);
        Assert.Contains("shortened", json.GetProperty("note").GetString());
        var write = await Call(tools, "write_file", new { path = "min.js", content = "x", expected_sha256 = json.GetProperty("sha256").GetString() });
        Assert.True(write.IsError);
        var edit = await Call(tools, "edit_file", new { path = "min.js", old_text = "var a=1;", new_text = "var a=2;" });
        Assert.False(edit.IsError, edit.Content);
    }

    [Fact]
    public async Task FailedEditsShowTheCurrentTextAndAmbiguousOnesTheirLines()
    {
        await File.WriteAllTextAsync(FilePath("H.cs"), "void A()\n{\n    Run(1);\n}\nvoid B()\n{\n    Run(1);\n}\n");
        var tools = Workspace();
        await Read(tools, "H.cs");
        var missing = await Call(tools, "edit_file", new { path = "H.cs", old_text = "void A()\n{\n    Run(2);", new_text = "x" });
        Assert.True(missing.IsError);
        Assert.Contains("The file currently has at line 1:\n1\tvoid A()\n2\t{\n3\t    Run(1);", missing.Content);
        var twice = await Call(tools, "edit_file", new { path = "H.cs", old_text = "    Run(1);", new_text = "    Run(3);" });
        Assert.True(twice.IsError);
        Assert.Contains("occurs 2 times (starting at line 3, 7)", twice.Content);
    }

    [Fact]
    public async Task ReplaceBetweenReplacesWholeLinesFromStartToEnd()
    {
        await File.WriteAllTextAsync(FilePath("R.cs"), "class R\r\n{\r\n    void A()\r\n    {\r\n        Old();\r\n    }\r\n    void B() { }\r\n}\r\n");
        var tools = Workspace();
        await Read(tools, "R.cs");
        var result = await Call(tools, "replace_between", new { path = "R.cs", start_line = "void A()", end_line = "}", new_text = "    void A() => New();" });
        Assert.False(result.IsError, result.Content);
        Assert.Contains("\"replaced_lines\":\"3-6\"", result.Content);
        Assert.Equal("class R\r\n{\r\n    void A() => New();\r\n    void B() { }\r\n}\r\n", await File.ReadAllTextAsync(FilePath("R.cs")));
        var missingEnd = await Call(tools, "replace_between", new { path = "R.cs", start_line = "void B() { }", end_line = "void C()", new_text = "" });
        Assert.True(missingEnd.IsError);
        Assert.Contains("end_line was not found", missingEnd.Content);
        await File.WriteAllTextAsync(FilePath("T.cs"), "if (a)\n{\n}\nif (b)\n{\n}\n");
        await Read(tools, "T.cs");
        var ambiguous = await Call(tools, "replace_between", new { path = "T.cs", start_line = "{", end_line = "}", new_text = "" });
        Assert.True(ambiguous.IsError);
        Assert.Contains("occurs 2 times (lines 2, 5)", ambiguous.Content);
    }

    [Fact]
    public async Task AmbiguousOldTextNeverFallsBackToADifferentlyIndentedBlock()
    {
        var original = "    if (x)\n    {\n        return null;\n    }\n    if (x)\n    {\n        return null;\n    }\n        if (x)\n        {\n            return null;\n        }\n";
        await File.WriteAllTextAsync(FilePath("M.cs"), original);
        var tools = Workspace();
        await Read(tools, "M.cs");
        var edit = await Call(tools, "edit_file", new { path = "M.cs", old_text = "    if (x)\n    {\n        return null;\n    }", new_text = "    if (y) { }" });
        Assert.True(edit.IsError);
        Assert.Contains("occurs 2 times", edit.Content);
        Assert.Equal(original, await File.ReadAllTextAsync(FilePath("M.cs")));
    }

    [Fact]
    public async Task NumberedOldTextWithUnnumberedInsertedLinesDoesNotWriteNumbers()
    {
        await File.WriteAllTextAsync(FilePath("N.cs"), "a\nfoo\nbar\n");
        var tools = Workspace();
        await Read(tools, "N.cs");
        var edit = await Call(tools, "edit_file", new { path = "N.cs", old_text = "2\tfoo\n3\tbar", new_text = "2\tfoo\n\tinserted\n3\tbar" });
        Assert.False(edit.IsError, edit.Content);
        Assert.Equal("a\nfoo\n\tinserted\nbar\n", await File.ReadAllTextAsync(FilePath("N.cs")));
    }

    [Fact]
    public async Task ReleasedCachesFallBackToAReread()
    {
        await File.WriteAllTextAsync(FilePath("K.cs"), "one\n");
        var tools = Workspace();
        await Read(tools, "K.cs");
        tools.ReleaseRunCaches();
        await File.WriteAllTextAsync(FilePath("K.cs"), "two\n");
        var stale = await Call(tools, "edit_file", new { path = "K.cs", old_text = "two", new_text = "2" });
        Assert.True(stale.IsError);
        Assert.Equal("File changed since you read it. Read it again before editing.", stale.Content);
    }

    [Fact]
    public void LineNumberPrefixesMustBeConsecutive()
    {
        Assert.Equal("a\nb", WorkspaceTools.StripLineNumbers("7\ta\n8\tb"));
        Assert.Null(WorkspaceTools.StripLineNumbers("7\ta\n9\tb"));
        Assert.Null(WorkspaceTools.StripLineNumbers("a\tb"));
    }
}

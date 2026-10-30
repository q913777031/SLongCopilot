using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

/// <summary>工具行后的结果摘要：常见工具给出简短中文说明，无法解析时为空。</summary>
public sealed class ToolSummaryTests
{
    [Theory]
    [InlineData("read_file", """{"total_lines":120,"content":""}""", "120 行")]
    [InlineData("read_file", """{"total_lines":3000,"start_line":1,"end_line":2000}""", "第 1–2,000 行，共 3,000 行")]
    [InlineData("edit_file", """{"lines_added":3,"lines_removed":1,"compile_errors":[]}""", "+3 −1")]
    [InlineData("multi_edit", """{"lines_added":2,"lines_removed":2,"compile_errors":[{},{}],"match_notes":"x"}""", "+2 −2，2 个编译错误（容错匹配）")]
    [InlineData("search_files", """{"matches":[{},{}],"truncated":true}""", "2+ 处匹配")]
    [InlineData("find_references", """{"results":[]}""", "未找到")]
    [InlineData("list_directory", """["a","b/"]""", "2 项")]
    [InlineData("build_solution", """{"succeeded":false,"diagnostics":[{"severity":"error"},{"severity":"warning"}]}""", "编译失败，1 个错误，1 个警告")]
    [InlineData("run_tests", """{"total":10,"passed":9,"failed":1}""", "9/10 通过，1 个失败")]
    [InlineData("run_command", """{"exit_code":0}""", "退出码 0")]
    [InlineData("read_file", "not json", "")]
    [InlineData("mcp__x__y", """{"a":1}""", "")]
    public void DescribesResults(string tool, string content, string expected) => Assert.Equal(expected, ToolSummary.Describe(tool, content));

    [Fact]
    public void DetailListsFieldsAndKeepsMultilineTextReadable()
    {
        Assert.Equal("path: A.cs\ntotal_lines: 2\ncontent:\n1\ta\n2\tb", ToolSummary.Detail("""{"path":"A.cs","total_lines":2,"note":null,"content":"1\ta\n2\tb"}"""));
        Assert.Equal("plain error", ToolSummary.Detail("plain error"));
        Assert.EndsWith("(5 more characters)", ToolSummary.Detail(new string('x', 15), 10));
    }
}

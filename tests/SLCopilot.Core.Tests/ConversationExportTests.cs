using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class ConversationExportTests
{
    [Fact]
    public void ExportsPromptsAnswersActivitiesAndCode()
    {
        var markdown = ConversationExport.ToMarkdown("修复空引用", [
            new("user", "你 · 智能体", "修复 A.cs 的空引用", [new("activity", "⧉ 当前文件 A.cs")]),
            new("assistant", "SLCopilot 智能体", "", [
                new("activity", "▸ 读取 A.cs  ⎿ 12 行"), new("error", "✗ 未完成"),
                new("markdown", "`A.cs`\n\n```diff\n-a\n+b\n```"), new("code", "var x = 1;")]),
        ], new DateTimeOffset(2026, 10, 29, 9, 30, 0, TimeSpan.Zero));
        Assert.Equal("# 修复空引用\n\n导出时间：2026-10-29 09:30\n\n## 你 · 智能体\n\n修复 A.cs 的空引用\n\n> ⧉ 当前文件 A.cs  \n\n" +
            "## SLCopilot 智能体\n\n> ▸ 读取 A.cs  ⎿ 12 行  \n> ⚠ ✗ 未完成  \n\n`A.cs`\n\n```diff\n-a\n+b\n```\n\n```\nvar x = 1;\n```\n", markdown);
    }
}

using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class SelfCheckReportTests
{
    [Fact]
    public void ReportSummarizesFailuresAndEscapesTableCells()
    {
        var markdown = SelfCheckReport.ToMarkdown(
        [
            new("服务连通", CheckLevel.Fail, "HTTP 401|密钥无效\n第二行"),
            new("Git", CheckLevel.Warn, "找不到 git"),
            new("版本", CheckLevel.Info, "0.4.0"),
        ], "abc123");
        Assert.StartsWith("**自检发现 1 项失败、1 项警告。**", markdown);
        Assert.Contains("| 服务连通 | ❌ 失败 | HTTP 401\\|密钥无效 第二行 |", markdown);
        Assert.EndsWith("`abc123`", markdown);
        Assert.StartsWith("**自检全部通过。**", SelfCheckReport.ToMarkdown([new("a", CheckLevel.Pass, "ok")], "x"));
    }
}

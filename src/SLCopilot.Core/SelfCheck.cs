using System.Text;

namespace SLCopilot.Core;

public enum CheckLevel { Pass, Warn, Fail, Info }

/// <summary>自检的一项结果。<see cref="Detail"/> 面向用户，不得包含密钥。</summary>
public sealed record CheckResult(string Name, CheckLevel Level, string Detail);

/// <summary>自检报告的格式化。</summary>
public static class SelfCheckReport
{
    public static string ToMarkdown(IReadOnlyList<CheckResult> results, string runId)
    {
        var text = new StringBuilder();
        var fails = results.Count(r => r.Level == CheckLevel.Fail);
        var warns = results.Count(r => r.Level == CheckLevel.Warn);
        text.Append(fails > 0 ? $"**自检发现 {fails} 项失败、{warns} 项警告。**" : warns > 0 ? $"**自检通过，有 {warns} 项警告。**" : "**自检全部通过。**")
            .Append("\n\n| 项目 | 结果 | 说明 |\n| --- | --- | --- |\n");
        foreach (var result in results)
            text.Append("| ").Append(Escape(result.Name)).Append(" | ").Append(Label(result.Level)).Append(" | ").Append(Escape(result.Detail)).Append(" |\n");
        return text.Append("\n运行日志中的编号：`").Append(runId).Append('`').ToString();
    }

    public static string Label(CheckLevel level) => level switch
    {
        CheckLevel.Pass => "✅ 通过",
        CheckLevel.Warn => "⚠️ 警告",
        CheckLevel.Fail => "❌ 失败",
        _ => "ℹ️ 信息",
    };

    private static string Escape(string text) => text.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
}

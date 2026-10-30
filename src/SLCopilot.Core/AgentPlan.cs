using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SLCopilot.Core;

/// <summary>计划中的一步。<see cref="Status"/> 为 done、doing 或 todo。</summary>
public sealed record PlanStep(string Text, string Status);

/// <summary>
/// 智能体的任务计划（update_plan 工具）。每行一步，以 done: / doing: / todo: 开头；最多 30 步、每步 200 字符、同时只能有一步 doing。
/// 计划只用于向用户展示进度，不影响工具权限。
/// </summary>
public static class AgentPlan
{
    public const int MaxSteps = 60, MaxStepChars = 200;
    private static readonly Regex Line = new(@"^\s*(?:[-*]\s*)?(?<status>done|doing|todo)\s*[:：]\s*(?<text>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>解析计划文本；格式不符时抛出 <see cref="WorkspaceException"/>，错误信息说明正确格式。</summary>
    public static IReadOnlyList<PlanStep> Parse(string plan)
    {
        var steps = new List<PlanStep>();
        foreach (var raw in (plan ?? "").Replace("\r", "").Split('\n'))
        {
            if (raw.Trim().Length == 0) continue;
            var match = Line.Match(raw);
            if (!match.Success) throw new WorkspaceException("Each plan line must start with done:, doing: or todo:, for example 'doing: Add validation'.");
            var text = match.Groups["text"].Value;
            steps.Add(new(text.Length > MaxStepChars ? text.Substring(0, MaxStepChars) + "…" : text, match.Groups["status"].Value.ToLowerInvariant()));
        }
        if (steps.Count == 0) throw new WorkspaceException("The plan is empty. Provide at least one step.");
        if (steps.Count > MaxSteps) throw new WorkspaceException($"Keep the plan to at most {MaxSteps} steps.");
        if (steps.Count(s => s.Status == "doing") > 1) throw new WorkspaceException("Only one step can be doing at a time.");
        return steps;
    }

    public static string Serialize(IReadOnlyList<PlanStep> steps) => JsonSerializer.Serialize(steps);

    public static IReadOnlyList<PlanStep> Deserialize(string json) => JsonSerializer.Deserialize<List<PlanStep>>(json) ?? [];

    /// <summary>界面显示用的 Markdown 清单。</summary>
    public static string ToMarkdown(IReadOnlyList<PlanStep> steps)
    {
        var text = new StringBuilder($"**计划**（已完成 {steps.Count(s => s.Status == "done")}/{steps.Count}）\n\n");
        foreach (var step in steps)
            text.Append("- ").Append(step.Status switch { "done" => "✅ ", "doing" => "▶ ", _ => "⬜ " }).Append(step.Text.Replace("\n", " ")).Append('\n');
        return text.ToString();
    }
}

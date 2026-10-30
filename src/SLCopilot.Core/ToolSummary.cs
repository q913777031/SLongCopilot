using System.Globalization;
using System.Text.Json;

namespace SLCopilot.Core;

/// <summary>工具成功结果的一句话摘要，显示在界面的工具行之后（仿 Claude Code 的结果行）。只用于显示，不发送给模型。</summary>
public static class ToolSummary
{
    /// <summary>耗时 2 秒以上的工具在摘要后注明用时（如编译、测试、全解决方案查找引用），便于看出慢在哪里。</summary>
    public static string WithDuration(string summary, int milliseconds) =>
        milliseconds < 2000 ? summary : (summary.Length == 0 ? "" : summary + " · ") + (milliseconds / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " 秒";

    /// <summary>
    /// 工具结果的可读形式（界面中展开工具行时显示）：JSON 对象逐个字段列出，多行文本原样换行显示；不是 JSON 时原样返回。
    /// 超过 <paramref name="maxChars"/> 个字符时截断并注明。
    /// </summary>
    public static string Detail(string content, int maxChars = 8000)
    {
        string text;
        try
        {
            using var document = JsonDocument.Parse(content);
            var builder = new System.Text.StringBuilder();
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Null) continue;
                    builder.Append(property.Name).Append(':');
                    if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } value)
                        builder.Append(value.IndexOf('\n') >= 0 ? "\n" + value : " " + value);
                    else builder.Append(' ').Append(property.Value.GetRawText());
                    builder.Append('\n');
                }
            else builder.Append(document.RootElement.GetRawText());
            text = builder.ToString().TrimEnd('\n');
        }
        catch (JsonException) { text = content; }
        return text.Length <= maxChars ? text : text.Substring(0, maxChars) + $"\n... ({text.Length - maxChars:N0} more characters)";
    }

    /// <summary>按工具名解析结果 JSON 给出摘要（中文）；结果不是 JSON、字段缺失或没有可说明的内容时返回空文本。</summary>
    public static string Describe(string tool, string content)
    {
        var summary = DescribeCore(tool, content);
        // 结果中附带了按路径生效的项目规则时注明。
        if (content.IndexOf("\"instructions\":[", StringComparison.Ordinal) >= 0)
            summary += (summary.Length > 0 ? " · " : "") + "附项目规则";
        return summary;
    }

    private static string DescribeCore(string tool, string content)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(content);
            root = document.RootElement.Clone();
        }
        catch (JsonException) { return ""; }
        var isObject = root.ValueKind == JsonValueKind.Object;
        int? Int(string name) => isObject && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
        int? Count(string name) => isObject && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : null;
        bool Flag(string name) => isObject && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
        string Plus() => Flag("truncated") ? "+" : "";
        string Results(int? count, string unit) => count switch { null => "", 0 => "未找到", var n => $"{N(n.Value)}{Plus()} {unit}" };

        switch (tool)
        {
            case "read_file":
                if (Flag("unchanged")) return "未变化，未重复发送";
                var total = Int("total_lines");
                var text = Int("start_line") is { } start && Int("end_line") is { } end && total is { } all && (start > 1 || end < all)
                    ? $"第 {N(start)}–{N(end)} 行，共 {N(all)} 行"
                    : total is { } lines ? $"{N(lines)} 行" : "";
                return Flag("unsaved_editor_changes") ? text + "（编辑器中未保存的内容）" : text;
            case "write_file" or "edit_file" or "multi_edit" or "replace_between" or "replace_symbol" or "insert_near_symbol":
                var change = Int("lines_added") is { } added && Int("lines_removed") is { } removed ? $"+{N(added)} −{N(removed)}" : "已写入";
                if (Count("compile_errors") is > 0 and var errors) change += $"，{errors} 个编译错误";
                if (isObject && root.TryGetProperty("match_notes", out _)) change += "（容错匹配）";
                if (Flag("user_edited")) change += "（你修改后的版本）";
                return change;
            case "delete_file": return "已删除";
            case "move_file": return "已移动";
            case "list_directory": return root.ValueKind == JsonValueKind.Array ? $"{N(root.GetArrayLength())} 项" : "";
            case "search_files": return Results(Count("matches"), "处匹配");
            case "find_files": return Results(Count("files"), "个文件");
            case "find_symbol" or "find_references" or "get_hierarchy" or "get_callers" or "get_callees" or "get_type_dependencies":
                return Results(Count("results"), "条结果");
            case "read_symbol": return Results(Count("results"), "个声明");
            case "get_outline": return Results(Count("symbols"), "个符号");
            case "get_project_graph": return Results(Count("projects"), "个项目");
            case "check_file":
                return Int("errors") is { } fileErrors ? $"{N(fileErrors)} 个错误，{N(Int("warnings") ?? 0)} 个警告" : "不属于已加载的项目";
            case "get_errors":
            case "build_solution":
                var errorCount = 0;
                var warningCount = 0;
                if (isObject && root.TryGetProperty("diagnostics", out var diagnostics) && diagnostics.ValueKind == JsonValueKind.Array)
                    foreach (var item in diagnostics.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("severity", out var severity))
                        {
                            if (severity.ValueEquals("error")) errorCount++;
                            else if (severity.ValueEquals("warning")) warningCount++;
                        }
                var counts = $"{N(errorCount)}{Plus()} 个错误，{N(warningCount)} 个警告";
                if (tool == "get_errors") return counts;
                return Flag("canceled") ? "编译已取消" : Flag("succeeded") ? "编译成功，" + counts : "编译失败，" + counts;
            case "run_tests":
                if (Int("total") is not { } tests || tests == 0) return "没有测试结果";
                return $"{N(Int("passed") ?? 0)}/{N(tests)} 通过" + (Int("failed") is > 0 and var failed ? $"，{N(failed)} 个失败" : "");
            case "run_command":
                if (Flag("timed_out")) return "已超时";
                return Int("exit_code") is { } code ? $"退出码 {code}" : "";
            default:
                return "";
        }
    }
}

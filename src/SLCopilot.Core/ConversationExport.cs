using System.Text;

namespace SLCopilot.Core;

/// <summary>把对话导出为 Markdown（/export）：提问与回答按顺序排列，工具记录为引用块，代码与差异保持原样。只做文本转换。</summary>
public static class ConversationExport
{
    public static string ToMarkdown(string title, IEnumerable<ConversationEntry> entries, DateTimeOffset exportedAt)
    {
        var builder = new StringBuilder();
        builder.Append("# ").Append(title.Length == 0 ? "SLCopilot 对话" : title).Append("\n\n");
        builder.Append("导出时间：").Append(exportedAt.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)).Append("\n\n");
        foreach (var entry in entries)
        {
            builder.Append("## ").Append(entry.Header.Length == 0 ? entry.Role : entry.Header).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(entry.Text)) builder.Append(entry.Text.TrimEnd()).Append("\n\n");
            var quoting = false;
            foreach (var part in entry.Parts ?? [])
            {
                var isActivity = part.Kind is "activity" or "error";
                if (quoting && !isActivity) { builder.Append('\n'); quoting = false; }
                switch (part.Kind)
                {
                    case "activity":
                    case "error":
                        builder.Append("> ").Append(part.Kind == "error" ? "⚠ " : "").Append(part.Text.Replace("\n", "\n> ")).Append("  \n");
                        quoting = true;
                        break;
                    case "code":
                        builder.Append("```\n").Append(part.Text.TrimEnd('\n')).Append("\n```\n\n");
                        break;
                    default:
                        if (part.Text.Trim().Length > 0) builder.Append(part.Text.TrimEnd()).Append("\n\n");
                        break;
                }
            }
            if (quoting) builder.Append('\n');
        }
        return builder.ToString().TrimEnd() + "\n";
    }
}

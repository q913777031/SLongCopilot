using System.Text;

namespace SLCopilot.Core;

/// <summary>Bounded, human-readable line diff for review, not a patch application format.</summary>
public static class ChangePreview
{
    /// <summary>LCS 计算的最大单元数（去掉相同的首尾行后，旧行数 × 新行数）；超过时按整段替换计数，可能偏大。</summary>
    private const long MaxLcsCells = 4_000_000;

    /// <summary>
    /// 统计新增与删除的行数（换行符差异不计）：先去掉相同的首尾行，中间部分按最长公共子序列计算；
    /// 中间部分过大时把它整体计为删除与新增。
    /// </summary>
    public static (int Added, int Removed) CountLines(string before, string after)
    {
        var oldLines = before.Replace("\r\n", "\n").Split('\n');
        var newLines = after.Replace("\r\n", "\n").Split('\n');
        var start = 0;
        while (start < oldLines.Length && start < newLines.Length && oldLines[start] == newLines[start]) start++;
        var oldEnd = oldLines.Length;
        var newEnd = newLines.Length;
        while (oldEnd > start && newEnd > start && oldLines[oldEnd - 1] == newLines[newEnd - 1]) { oldEnd--; newEnd--; }
        int n = oldEnd - start, m = newEnd - start;
        if (n == 0 || m == 0 || (long)n * m > MaxLcsCells) return (m, n);
        var previous = new int[m + 1];
        var current = new int[m + 1];
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
                current[j] = oldLines[start + i - 1] == newLines[start + j - 1] ? previous[j - 1] + 1 : Math.Max(previous[j], current[j - 1]);
            (previous, current) = (current, previous);
        }
        var common = previous[m];
        return (m - common, n - common);
    }

    /// <summary>逐行差异计算的最大单元数（去掉相同的首尾行后，旧行数 × 新行数）；超过时把中间部分显示为整段删除与新增。</summary>
    private const long MaxDiffCells = 1_000_000;

    /// <summary>
    /// 紧凑的逐行差异：每行以 "-"、"+" 或空格开头；每个变化片段前有 "@@ -旧行号 +新行号 @@"，前后各保留 <paramref name="context"/> 行。
    /// 超过 <paramref name="maxLines"/> 行时截断（<c>Truncated</c> 为 true）。换行符差异不计；没有变化时返回空文本。
    /// </summary>
    public static (string Text, bool Truncated) UnifiedDiff(string before, string after, int context = 2, int maxLines = 60)
    {
        var a = before.Length == 0 ? [] : before.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var b = after.Length == 0 ? [] : after.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) start++;
        int oldEnd = a.Length, newEnd = b.Length;
        while (oldEnd > start && newEnd > start && a[oldEnd - 1] == b[newEnd - 1]) { oldEnd--; newEnd--; }
        if (start == oldEnd && start == newEnd) return ("", false);
        var ops = new List<(char Op, int Old, int New)>();
        for (var i = 0; i < start; i++) ops.Add((' ', i, i));
        int n = oldEnd - start, m = newEnd - start;
        if ((long)n * m > MaxDiffCells)
        {
            for (var i = start; i < oldEnd; i++) ops.Add(('-', i, -1));
            for (var j = start; j < newEnd; j++) ops.Add(('+', -1, j));
        }
        else
        {
            // lcs[i, j]：a[start + i ..] 与 b[start + j ..] 的最长公共子序列长度（按行优先展开为一维）。
            var width = m + 1;
            var lcs = new int[(n + 1) * width];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    lcs[i * width + j] = a[start + i] == b[start + j] ? lcs[(i + 1) * width + j + 1] + 1 : Math.Max(lcs[(i + 1) * width + j], lcs[i * width + j + 1]);
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[start + x] == b[start + y]) { ops.Add((' ', start + x, start + y)); x++; y++; }
                else if (y >= m || (x < n && lcs[(x + 1) * width + y] >= lcs[x * width + y + 1])) { ops.Add(('-', start + x, -1)); x++; }
                else { ops.Add(('+', -1, start + y)); y++; }
            }
        }
        for (int i = oldEnd, j = newEnd; i < a.Length; i++, j++) ops.Add((' ', i, j));

        var show = new bool[ops.Count];
        for (var k = 0; k < ops.Count; k++)
            if (ops[k].Op != ' ')
                for (var c = Math.Max(0, k - context); c <= Math.Min(ops.Count - 1, k + context); c++) show[c] = true;
        var result = new StringBuilder();
        var lines = 0;
        int oldLine = 0, newLine = 0;
        for (var k = 0; k < ops.Count; k++)
        {
            var (op, o, w) = ops[k];
            if (show[k])
            {
                if (k == 0 || !show[k - 1]) result.Append("@@ -").Append(oldLine + 1).Append(" +").Append(newLine + 1).Append(" @@\n");
                if (lines >= maxLines)
                {
                    var rest = ops.Skip(k).Count(p => p.Op != ' ');
                    if (rest > 0) return (result.Append("... ").Append(rest).Append(" more changed lines\n").ToString().TrimEnd('\n'), true);
                    break;
                }
                var text = op == '+' ? b[w] : a[o];
                result.Append(op).Append(text.Length > 300 ? text.Substring(0, 300) + " ..." : text).Append('\n');
                lines++;
            }
            if (op != '+') oldLine++;
            if (op != '-') newLine++;
        }
        return (result.ToString().TrimEnd('\n'), false);
    }

    public static string Create(string path, string before, string after, int maxLength = 10000)
    {
        var oldLines = before.Replace("\r\n", "\n").Split('\n');
        var newLines = after.Replace("\r\n", "\n").Split('\n');
        var start = 0;
        while (start < oldLines.Length && start < newLines.Length && oldLines[start] == newLines[start]) start++;
        var oldEnd = oldLines.Length;
        var newEnd = newLines.Length;
        while (oldEnd > start && newEnd > start && oldLines[oldEnd - 1] == newLines[newEnd - 1]) { oldEnd--; newEnd--; }
        var result = new StringBuilder($"--- {path} (before)\n+++ {path} (after)\n@@ starting line {start + 1} @@\n");
        if (start == oldLines.Length && start == newLines.Length) return result + "[No visible line changes]\n";
        for (var i = Math.Max(0, start - 3); i < start; i++) Append(' ', oldLines[i]);
        for (var i = start; i < oldEnd; i++) if (!Append('-', oldLines[i])) break;
        for (var i = start; i < newEnd; i++) if (!Append('+', newLines[i])) break;
        for (var i = newEnd; i < Math.Min(newEnd + 3, newLines.Length); i++) Append(' ', newLines[i]);
        return result.ToString();

        bool Append(char prefix, string line)
        {
            if (result.Length >= maxLength) return false;
            result.Append(prefix).Append(line[..Math.Min(line.Length, 1000)]);
            if (line.Length > 1000) result.Append(" [line truncated]");
            result.AppendLine();
            if (result.Length >= maxLength) result.AppendLine("[Preview truncated; inspect the file and backup for the complete change]");
            return result.Length < maxLength;
        }
    }
}

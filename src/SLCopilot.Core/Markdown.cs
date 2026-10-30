using System.Text;
using System.Text.RegularExpressions;

namespace SLCopilot.Core;

public enum MdBlockKind { Paragraph, Heading1, Heading2, Heading3, Bullet, Numbered, Quote, Code, Table, Rule }

public enum MdInlineKind { Text, Bold, Italic, Code, Link, Break }

/// <summary>行内片段；<see cref="Url"/> 仅 Link 有值。</summary>
public sealed record MdInline(MdInlineKind Kind, string Text, string Url = "");

/// <summary>一个渲染块。<see cref="Inlines"/> 仅对文本类块有效；代码块与表格只用 <see cref="Text"/>。</summary>
public sealed record MdBlock(MdBlockKind Kind, string Text, IReadOnlyList<MdInline> Inlines, string Language = "", string Marker = "", int Level = 0)
{
    /// <summary>块内是否含需要格式化渲染的行内元素；否则可用单个自动换行文本显示。</summary>
    public bool HasFormatting => Inlines.Any(i => i.Kind is not (MdInlineKind.Text or MdInlineKind.Break));

    /// <summary>表格的单元格（行 → 列 → 行内元素），第一行为表头；不是表格时为 null。<see cref="Text"/> 仍为对齐后的纯文本（用于复制）。</summary>
    public IReadOnlyList<IReadOnlyList<IReadOnlyList<MdInline>>>? Rows { get; init; }
}

/// <summary>
/// 面向聊天回答的轻量 Markdown 解析（CommonMark 子集）：标题、段落、列表、引用、围栏代码、表格、分隔线；
/// 行内支持粗体、斜体、行内代码与链接。不支持 HTML，原样作为文本。无法识别的语法按普通文本处理，不抛异常。
/// </summary>
public static class Markdown
{
    public static IReadOnlyList<MdBlock> Parse(string markdown)
    {
        var blocks = new List<MdBlock>();
        var lines = (markdown ?? "").Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            var fence = FenceRegex().Match(line);
            if (fence.Success)
            {
                Flush();
                var marker = fence.Groups["fence"].Value;
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith(marker, StringComparison.Ordinal); i++) code.Add(lines[i]);
                blocks.Add(new(MdBlockKind.Code, string.Join("\n", code), [], fence.Groups["lang"].Value.Trim()));
                continue;
            }
            if (trimmed.Length == 0) { Flush(); continue; }
            if (RuleRegex().IsMatch(trimmed)) { Flush(); blocks.Add(new(MdBlockKind.Rule, "", [])); continue; }
            var heading = HeadingRegex().Match(trimmed);
            if (heading.Success)
            {
                Flush();
                var kind = heading.Groups[1].Length switch { 1 => MdBlockKind.Heading1, 2 => MdBlockKind.Heading2, _ => MdBlockKind.Heading3 };
                blocks.Add(TextBlock(kind, heading.Groups[2].Value.TrimEnd('#', ' ')));
                continue;
            }
            if (trimmed.StartsWith("|", StringComparison.Ordinal))
            {
                Flush();
                var table = new List<string>();
                for (; i < lines.Length && lines[i].Trim().StartsWith("|", StringComparison.Ordinal); i++) table.Add(lines[i].Trim());
                i--;
                blocks.Add(new(MdBlockKind.Table, FormatTable(table), []) { Rows = TableRows(table) });
                continue;
            }
            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                Flush();
                var quote = new List<string>();
                for (; i < lines.Length && lines[i].Trim().StartsWith(">", StringComparison.Ordinal); i++) quote.Add(lines[i].Trim().Substring(1).TrimStart());
                i--;
                blocks.Add(TextBlock(MdBlockKind.Quote, string.Join("\n", quote)));
                continue;
            }
            var item = ListRegex().Match(line);
            if (item.Success)
            {
                Flush();
                var text = new StringBuilder(item.Groups["text"].Value);
                // 缩进的续行并入当前列表项。
                while (i + 1 < lines.Length && lines[i + 1].Length > 0 && char.IsWhiteSpace(lines[i + 1][0]) &&
                       !ListRegex().IsMatch(lines[i + 1]) && !FenceRegex().IsMatch(lines[i + 1]))
                    text.Append(' ').Append(lines[++i].Trim());
                var bullet = item.Groups["bullet"].Value;
                var numbered = char.IsDigit(bullet[0]);
                var level = Math.Min(4, item.Groups["indent"].Value.Replace("\t", "  ").Length / 2);
                blocks.Add(TextBlock(numbered ? MdBlockKind.Numbered : MdBlockKind.Bullet, text.ToString(),
                    numbered ? bullet.TrimEnd(')', '.') + "." : level == 0 ? "•" : "◦", level));
                continue;
            }
            paragraph.Add(trimmed);
        }
        Flush();
        return blocks;

        void Flush()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(TextBlock(MdBlockKind.Paragraph, string.Join("\n", paragraph)));
            paragraph.Clear();
        }
    }

    private static MdBlock TextBlock(MdBlockKind kind, string text, string marker = "", int level = 0)
    {
        var inlines = ParseInlines(text);
        return new(kind, string.Concat(inlines.Select(i => i.Kind == MdInlineKind.Break ? "\n" : i.Text)), inlines, "", marker, level);
    }

    public static IReadOnlyList<MdInline> ParseInlines(string text)
    {
        var result = new List<MdInline>();
        var plain = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\n') { Add(MdInlineKind.Break, "\n"); i++; continue; }
            if (c == '\\' && i + 1 < text.Length && "\\`*_[]()#+-.!|>~".IndexOf(text[i + 1]) >= 0) { plain.Append(text[i + 1]); i += 2; continue; }
            if (c == '`')
            {
                var ticks = 1;
                while (i + ticks < text.Length && text[i + ticks] == '`') ticks++;
                var close = text.IndexOf(new string('`', ticks), i + ticks, StringComparison.Ordinal);
                if (close > i)
                {
                    Add(MdInlineKind.Code, text[(i + ticks)..close].Trim());
                    i = close + ticks;
                    continue;
                }
            }
            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                var marker = new string(c, 2);
                var close = text.IndexOf(marker, i + 2, StringComparison.Ordinal);
                if (close > i + 2 && !char.IsWhiteSpace(text[i + 2]))
                {
                    Add(MdInlineKind.Bold, text[(i + 2)..close].Trim('*', '_'));
                    i = close + 2;
                    continue;
                }
            }
            if ((c == '*' || c == '_') && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) &&
                (c == '*' || i == 0 || !char.IsLetterOrDigit(text[i - 1])))
            {
                var close = text.IndexOf(c, i + 1);
                if (close > i + 1 && !char.IsWhiteSpace(text[close - 1]) &&
                    (c == '*' || close + 1 >= text.Length || !char.IsLetterOrDigit(text[close + 1])))
                {
                    Add(MdInlineKind.Italic, text[(i + 1)..close]);
                    i = close + 1;
                    continue;
                }
            }
            if (c == '[')
            {
                var link = LinkRegex().Match(text, i);
                if (link.Success && link.Index == i)
                {
                    FlushPlain();
                    result.Add(new(MdInlineKind.Link, link.Groups[1].Value, link.Groups[2].Value));
                    i += link.Length;
                    continue;
                }
            }
            plain.Append(c);
            i++;
        }
        FlushPlain();
        return result;

        void Add(MdInlineKind kind, string value)
        {
            FlushPlain();
            if (value.Length > 0) result.Add(new(kind, value));
        }
        void FlushPlain()
        {
            if (plain.Length == 0) return;
            result.Add(new(MdInlineKind.Text, plain.ToString()));
            plain.Clear();
        }
    }

    /// <summary>
    /// 把行内元素拆成可在 WrapPanel 中逐个换行的片段：西文按单词（含尾随空格），中日韩字符逐字；行内代码与换行保持整体。
    /// 片段超过 <paramref name="maxTokens"/> 时返回 null，调用方应退回纯文本显示以控制界面元素数量。
    /// </summary>
    public static IReadOnlyList<MdInline>? Tokenize(IReadOnlyList<MdInline> inlines, int maxTokens = 1500)
    {
        var tokens = new List<MdInline>();
        foreach (var inline in inlines)
        {
            if (inline.Kind is MdInlineKind.Code or MdInlineKind.Break) { tokens.Add(inline); continue; }
            var current = new StringBuilder();
            foreach (var c in inline.Text)
            {
                if (IsCjk(c))
                {
                    Emit();
                    current.Append(c);
                    Emit();
                }
                else
                {
                    if (current.Length > 0 && !char.IsWhiteSpace(c) && char.IsWhiteSpace(current[^1])) Emit();
                    current.Append(c);
                }
            }
            Emit();

            void Emit()
            {
                if (current.Length == 0) return;
                tokens.Add(new(inline.Kind, current.ToString(), inline.Url));
                current.Clear();
            }
        }
        return tokens.Count > maxTokens ? null : tokens;
    }

    private static bool IsCjk(char c) => c is >= '⺀' and <= '鿿' or >= '가' and <= '힯' or >= '豈' and <= '﫿' or >= '＀' and <= '￯';

    /// <summary>表格各行的单元格（跳过分隔行），单元格内容按行内 Markdown 解析；反引号内与转义的竖线不拆分。</summary>
    private static IReadOnlyList<IReadOnlyList<IReadOnlyList<MdInline>>> TableRows(List<string> rows) => rows
        .Where(r => !SeparatorRowRegex().IsMatch(r))
        .Select(r => (IReadOnlyList<IReadOnlyList<MdInline>>)SplitRow(r).Select(c => ParseInlines(c)).ToList())
        .ToList();

    private static List<string> SplitRow(string row)
    {
        var text = row.Trim();
        if (text.StartsWith("|", StringComparison.Ordinal)) text = text.Substring(1);
        if (text.EndsWith("|", StringComparison.Ordinal) && !text.EndsWith("\\|", StringComparison.Ordinal)) text = text.Substring(0, text.Length - 1);
        var cells = new List<string>();
        var current = new StringBuilder();
        var inCode = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] == '|') { current.Append('|'); i++; continue; }
            if (c == '`') inCode = !inCode;
            if (c == '|' && !inCode) { cells.Add(current.ToString().Trim()); current.Clear(); continue; }
            current.Append(c);
        }
        cells.Add(current.ToString().Trim());
        return cells;
    }

    private static string FormatTable(List<string> rows)
    {
        var cells = rows.Where(r => !SeparatorRowRegex().IsMatch(r))
            .Select(r => r.Trim().Trim('|').Split('|').Select(c => c.Trim().Replace("**", "").Replace("`", "")).ToArray())
            .ToList();
        var columns = cells.Max(r => r.Length);
        var widths = Enumerable.Range(0, columns).Select(c => cells.Max(r => c < r.Length ? DisplayWidth(r[c]) : 0)).ToArray();
        var output = new StringBuilder();
        for (var r = 0; r < cells.Count; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                var value = c < cells[r].Length ? cells[r][c] : "";
                output.Append(value).Append(' ', widths[c] - DisplayWidth(value) + (c < columns - 1 ? 2 : 0));
            }
            output.Append('\n');
            if (r == 0 && cells.Count > 1) output.Append(new string('─', widths.Sum() + 2 * (columns - 1))).Append('\n');
        }
        return output.ToString().TrimEnd('\n');
    }

    private static int DisplayWidth(string text) => text.Sum(c => IsCjk(c) ? 2 : 1);

    private static readonly Regex FenceRegexPattern = new(@"^\s{0,3}(?<fence>```|~~~)(?<lang>[^`]*)$", RegexOptions.CultureInvariant);
    private static Regex FenceRegex() => FenceRegexPattern;
    private static readonly Regex RuleRegexPattern = new(@"^(\*\s*){3,}$|^(-\s*){3,}$|^(_\s*){3,}$", RegexOptions.CultureInvariant);
    private static Regex RuleRegex() => RuleRegexPattern;
    private static readonly Regex HeadingRegexPattern = new(@"^(#{1,6})\s+(.*)$", RegexOptions.CultureInvariant);
    private static Regex HeadingRegex() => HeadingRegexPattern;
    private static readonly Regex ListRegexPattern = new(@"^(?<indent>\s*)(?<bullet>[-*+]|\d{1,3}[.)])\s+(?<text>.*)$", RegexOptions.CultureInvariant);
    private static Regex ListRegex() => ListRegexPattern;
    private static readonly Regex LinkRegexPattern = new(@"\[([^\]]+)\]\(([^)\s]+)\)", RegexOptions.CultureInvariant);
    private static Regex LinkRegex() => LinkRegexPattern;
    private static readonly Regex SeparatorRowRegexPattern = new(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$", RegexOptions.CultureInvariant);
    private static Regex SeparatorRowRegex() => SeparatorRowRegexPattern;
}

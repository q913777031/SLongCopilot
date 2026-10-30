using System.Text.RegularExpressions;

namespace SLCopilot.Core;

/// <summary>
/// 回答中可点击的代码引用：文件引用（<c>src/Foo.cs</c>、<c>Foo.cs:42</c>、<c>Foo.cs:42-50</c>、<c>Foo.cs(42)</c>）与符号引用（<c>OrderService.Save</c>、<c>OrderService</c>）。
/// 只做文本识别，不检查文件或符号是否存在。
/// </summary>
public static class CodeReference
{
    internal const string Extensions = "cs|vb|fs|xaml|axaml|razor|cshtml|csproj|vbproj|props|targets|sln|slnx|config|json|xml|resx|md|txt|sql|js|ts|tsx|jsx|css|scss|html|htm|" +
        "c|cc|cpp|cxx|h|hh|hpp|py|ps1|psm1|cmd|bat|sh|yml|yaml|toml|ini|editorconfig|gitignore";

    private static readonly Regex FilePattern = new(
        @"^(?<path>(?:[A-Za-z]:[\\/])?[^\s:*?""<>|()]*?[^\s:*?""<>|()\\/]\.(?:" + Extensions + @"))(?:(?::|#L)(?<line>\d{1,7})(?:[-–](?:L)?\d{1,7})?(?::\d{1,5})?|\((?<line>\d{1,7})(?:,\d{1,5})?\))?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>正文中带行号的文件引用（如 <c>src/Foo.cs:42</c>），用于在普通文字中识别；不带行号的路径在正文中不识别，避免误报。</summary>
    public static readonly Regex InlineFilePattern = new(
        @"(?<![\w/\\.])(?:[A-Za-z]:[\\/])?[\w.\-]+(?:[\\/][\w.\-]+)*\.(?:" + Extensions + @")(?::\d{1,7}(?:[-–]\d{1,7})?|\(\d{1,7}\))(?![\w])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SymbolPattern = new(
        @"^(?:[A-Za-z_][A-Za-z0-9_]*(?:<[^<>]*>)?\.)*[A-Za-z_][A-Za-z0-9_]*(?:<[^<>]*>)?(?:\([^()]*\))?$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> NotSymbols = new(StringComparer.Ordinal)
        { "String", "Task", "List", "Dictionary", "Exception", "Console", "Math", "Guid", "DateTime", "TimeSpan", "Action", "Func", "Object", "Int32", "Boolean" };

    /// <summary>识别文件引用；<paramref name="line"/> 为行号（没有时为 0）。</summary>
    public static bool TryParseFile(string text, out string path, out int line)
    {
        path = "";
        line = 0;
        var match = FilePattern.Match(text.Trim());
        if (!match.Success) return false;
        path = match.Groups["path"].Value;
        // UNC 与设备路径（\\server\share、//server）不作为可点击引用：打开它们会连接远程共享。
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) { path = ""; return false; }
        if (match.Groups["line"].Success) line = int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// 识别符号引用：带点的限定名（<c>OrderService.Save</c>、可带参数列表与泛型参数），或首字母大写且含小写字母的单个标识符（<c>OrderService</c>）。
    /// 常见的框架类型名、文件名与纯小写单词不识别。返回去掉参数列表的名称。
    /// </summary>
    public static bool TryParseSymbol(string text, out string name)
    {
        name = "";
        text = text.Trim();
        if (text.Length is < 3 or > 200 || !SymbolPattern.IsMatch(text) || TryParseFile(text, out _, out _)) return false;
        var bare = text.IndexOf('(') is var paren and >= 0 ? text.Substring(0, paren) : text;
        if (bare.IndexOf('.') < 0 && (!char.IsUpper(bare[0]) || !bare.Any(char.IsLower) || NotSymbols.Contains(bare))) return false;
        name = text;
        return true;
    }

    /// <summary>是否为可点击的引用（文件或符号）。</summary>
    public static bool IsReference(string text) => TryParseFile(text, out _, out _) || TryParseSymbol(text, out _);
}

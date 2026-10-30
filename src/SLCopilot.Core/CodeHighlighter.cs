namespace SLCopilot.Core;

public enum CodeTokenKind { Keyword, Type, String, Comment, Number, Preprocessor, Tag, Attribute }

/// <summary>代码中需要着色的一段。未覆盖的文本按普通文本显示。</summary>
public readonly record struct CodeToken(int Start, int Length, CodeTokenKind Kind);

/// <summary>
/// 轻量词法着色：按语言识别注释、字符串、数字、关键字（XML 识别标签与属性，JSON 区分键与值）。
/// 只用于显示，不保证语法完整；未知语言或超过 200,000 字符的代码不着色（返回空列表）。
/// </summary>
public static class CodeHighlighter
{
    public const int MaxChars = 200_000;

    private sealed record Language(string[] Keywords, string[] Types, string LineComment, bool BlockComments, bool HashPreprocessor,
        bool TripleQuotes = false, bool VerbatimStrings = false, bool CaseInsensitive = false);

    private static readonly Language CSharp = new(
        ["abstract", "as", "async", "await", "base", "break", "case", "catch", "checked", "class", "const", "continue", "default", "delegate", "do", "else",
         "enum", "event", "explicit", "extern", "false", "finally", "fixed", "for", "foreach", "get", "goto", "if", "implicit", "in", "init", "interface",
         "internal", "is", "lock", "namespace", "new", "null", "operator", "out", "override", "params", "partial", "private", "protected", "public",
         "readonly", "record", "ref", "required", "return", "sealed", "set", "sizeof", "stackalloc", "static", "struct", "switch", "this", "throw", "true",
         "try", "typeof", "unchecked", "unsafe", "using", "value", "var", "virtual", "void", "volatile", "when", "where", "while", "with", "yield", "and", "or", "not"],
        ["bool", "byte", "char", "decimal", "double", "dynamic", "float", "int", "long", "nint", "nuint", "object", "sbyte", "short", "string", "uint", "ulong", "ushort",
         "Task", "List", "Dictionary", "IEnumerable", "IReadOnlyList", "Action", "Func", "CancellationToken", "Exception", "String", "Console"],
        "//", true, true, VerbatimStrings: true);

    private static readonly Language Cpp = new(
        ["alignas", "auto", "break", "case", "catch", "class", "const", "constexpr", "continue", "default", "delete", "do", "else", "enum", "explicit",
         "extern", "false", "for", "friend", "goto", "if", "inline", "mutable", "namespace", "new", "noexcept", "nullptr", "operator", "override", "private",
         "protected", "public", "return", "sizeof", "static", "static_cast", "struct", "switch", "template", "this", "throw", "true", "try", "typedef",
         "typename", "union", "using", "virtual", "volatile", "while", "dynamic_cast", "reinterpret_cast", "const_cast"],
        ["bool", "char", "double", "float", "int", "long", "short", "signed", "unsigned", "void", "size_t", "std", "string", "vector", "uint8_t", "int32_t", "uint32_t", "int64_t", "uint64_t"],
        "//", true, true);

    private static readonly Language Java = new(
        ["abstract", "break", "case", "catch", "class", "continue", "default", "do", "else", "enum", "extends", "false", "final", "finally", "for", "fun",
         "if", "implements", "import", "instanceof", "interface", "new", "null", "package", "private", "protected", "public", "return", "static", "super",
         "switch", "synchronized", "this", "throw", "throws", "true", "try", "val", "var", "void", "while", "when", "object", "override", "func", "go",
         "defer", "chan", "select", "range", "type", "struct", "map", "let", "mut", "fn", "impl", "pub", "use", "mod", "match", "loop", "crate", "self", "trait"],
        ["boolean", "byte", "char", "double", "float", "int", "long", "short", "String", "Integer", "List", "Map", "string", "bool", "i32", "i64", "u8", "u32", "u64", "usize", "f64", "Vec", "Option", "Result"],
        "//", true, false);

    private static readonly Language Script = new(
        ["async", "await", "break", "case", "catch", "class", "const", "continue", "default", "delete", "do", "else", "enum", "export", "extends", "false",
         "finally", "for", "from", "function", "if", "import", "in", "instanceof", "interface", "let", "new", "null", "of", "return", "static", "super",
         "switch", "this", "throw", "true", "try", "type", "typeof", "undefined", "var", "void", "while", "yield", "implements", "private", "public", "readonly"],
        ["any", "boolean", "number", "string", "never", "unknown", "object", "Promise", "Array", "Record"],
        "//", true, false);

    private static readonly Language Python = new(
        ["and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else", "except", "False", "finally", "for", "from",
         "global", "if", "import", "in", "is", "lambda", "None", "nonlocal", "not", "or", "pass", "raise", "return", "True", "try", "while", "with", "yield", "self"],
        ["int", "str", "float", "bool", "list", "dict", "set", "tuple", "bytes", "object"],
        "#", false, false, TripleQuotes: true);

    private static readonly Language Sql = new(
        ["select", "from", "where", "insert", "into", "values", "update", "set", "delete", "create", "table", "alter", "drop", "index", "join", "left", "right",
         "inner", "outer", "on", "group", "by", "order", "having", "and", "or", "not", "null", "is", "as", "distinct", "union", "all", "primary", "key",
         "foreign", "references", "default", "begin", "commit", "rollback", "transaction", "case", "when", "then", "else", "end", "limit", "top", "in", "exists", "like"],
        ["int", "integer", "bigint", "text", "varchar", "nvarchar", "char", "real", "float", "decimal", "datetime", "date", "blob", "boolean", "bit"],
        "--", true, false, CaseInsensitive: true);

    private static readonly Language Shell = new(
        ["if", "then", "else", "elif", "fi", "for", "in", "do", "done", "while", "case", "esac", "function", "return", "echo", "exit", "export", "local",
         "param", "foreach", "switch", "try", "catch", "finally", "throw", "begin", "process", "end"],
        [], "#", false, false, CaseInsensitive: true);

    public static IReadOnlyList<CodeToken> Tokenize(string code, string? language)
    {
        if (string.IsNullOrEmpty(code) || code.Length > MaxChars) return [];
        switch ((language ?? "").Trim().ToLowerInvariant())
        {
            case "cs": case "csharp": case "c#": return Lex(code, CSharp);
            case "c": case "cpp": case "c++": case "h": case "hpp": case "cc": return Lex(code, Cpp);
            case "java": case "kotlin": case "kt": case "go": case "rust": case "rs": case "swift": return Lex(code, Java);
            case "js": case "javascript": case "ts": case "typescript": case "jsx": case "tsx": return Lex(code, Script);
            case "py": case "python": return Lex(code, Python);
            case "sql": return Lex(code, Sql);
            case "sh": case "bash": case "shell": case "ps1": case "powershell": case "pwsh": return Lex(code, Shell);
            case "json": case "jsonc": return Json(code);
            case "xml": case "xaml": case "html": case "csproj": case "props": case "targets": case "vsct": case "config": case "svg": return Xml(code);
            default: return [];
        }
    }

    private static List<CodeToken> Lex(string code, Language language)
    {
        var tokens = new List<CodeToken>();
        var keywords = new HashSet<string>(language.Keywords, language.CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var types = new HashSet<string>(language.Types, language.CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var lineStart = true;
        for (var i = 0; i < code.Length;)
        {
            var c = code[i];
            if (c == '\n') { lineStart = true; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            var atLineStart = lineStart;
            lineStart = false;
            if (Starts(code, i, language.LineComment) && !(language.HashPreprocessor && language.LineComment == "#"))
            {
                var end = LineEnd(code, i);
                tokens.Add(new(i, end - i, CodeTokenKind.Comment));
                i = end;
                continue;
            }
            if (language.BlockComments && Starts(code, i, "/*"))
            {
                var end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? code.Length : end + 2;
                tokens.Add(new(i, end - i, CodeTokenKind.Comment));
                i = end;
                continue;
            }
            if (language.HashPreprocessor && c == '#' && atLineStart)
            {
                var end = LineEnd(code, i);
                tokens.Add(new(i, end - i, CodeTokenKind.Preprocessor));
                i = end;
                continue;
            }
            if (language.TripleQuotes && (Starts(code, i, "\"\"\"") || Starts(code, i, "'''")))
            {
                var quote = code.Substring(i, 3);
                var end = code.IndexOf(quote, i + 3, StringComparison.Ordinal);
                end = end < 0 ? code.Length : end + 3;
                tokens.Add(new(i, end - i, CodeTokenKind.String));
                i = end;
                continue;
            }
            if (c is '"' or '\'' or '`' || (language.VerbatimStrings && c is '@' or '$' && i + 1 < code.Length && (code[i + 1] == '"' || (code[i + 1] is '@' or '$' && i + 2 < code.Length && code[i + 2] == '"'))))
            {
                var start = i;
                var verbatim = false;
                while (code[i] is '@' or '$') { verbatim |= code[i] == '@'; i++; }
                var quote = code[i];
                i++;
                while (i < code.Length)
                {
                    if (!verbatim && code[i] == '\\') { i += 2; continue; }
                    if (code[i] == quote)
                    {
                        if (verbatim && i + 1 < code.Length && code[i + 1] == quote) { i += 2; continue; }
                        i++;
                        break;
                    }
                    if (code[i] == '\n' && !verbatim && quote != '`') break;
                    i++;
                }
                i = Math.Min(i, code.Length);
                tokens.Add(new(start, i - start, CodeTokenKind.String));
                continue;
            }
            if (char.IsDigit(c))
            {
                var start = i;
                while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] is '.' or '_')) i++;
                tokens.Add(new(start, i - start, CodeTokenKind.Number));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '_')) i++;
                var word = code.Substring(start, i - start);
                if (keywords.Contains(word)) tokens.Add(new(start, word.Length, CodeTokenKind.Keyword));
                else if (types.Contains(word)) tokens.Add(new(start, word.Length, CodeTokenKind.Type));
                continue;
            }
            i++;
        }
        return tokens;
    }

    private static List<CodeToken> Json(string code)
    {
        var tokens = new List<CodeToken>();
        for (var i = 0; i < code.Length;)
        {
            var c = code[i];
            if (c == '"')
            {
                var start = i++;
                while (i < code.Length && code[i] != '"' && code[i] != '\n') i += code[i] == '\\' ? 2 : 1;
                i = Math.Min(i + 1, code.Length);
                var next = i;
                while (next < code.Length && char.IsWhiteSpace(code[next])) next++;
                tokens.Add(new(start, i - start, next < code.Length && code[next] == ':' ? CodeTokenKind.Attribute : CodeTokenKind.String));
                continue;
            }
            if (Starts(code, i, "//")) { var end = LineEnd(code, i); tokens.Add(new(i, end - i, CodeTokenKind.Comment)); i = end; continue; }
            if (char.IsDigit(c) || (c == '-' && i + 1 < code.Length && char.IsDigit(code[i + 1])))
            {
                var start = i++;
                while (i < code.Length && (char.IsDigit(code[i]) || code[i] is '.' or 'e' or 'E' or '+' or '-')) i++;
                tokens.Add(new(start, i - start, CodeTokenKind.Number));
                continue;
            }
            foreach (var literal in new[] { "true", "false", "null" })
                if (Starts(code, i, literal)) { tokens.Add(new(i, literal.Length, CodeTokenKind.Keyword)); i += literal.Length - 1; break; }
            i++;
        }
        return tokens;
    }

    private static List<CodeToken> Xml(string code)
    {
        var tokens = new List<CodeToken>();
        for (var i = 0; i < code.Length;)
        {
            if (Starts(code, i, "<!--"))
            {
                var end = code.IndexOf("-->", i + 4, StringComparison.Ordinal);
                end = end < 0 ? code.Length : end + 3;
                tokens.Add(new(i, end - i, CodeTokenKind.Comment));
                i = end;
                continue;
            }
            if (code[i] != '<') { i++; continue; }
            var nameStart = i;
            i++;
            if (i < code.Length && code[i] is '/' or '?' or '!') i++;
            while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] is ':' or '.' or '-' or '_')) i++;
            tokens.Add(new(nameStart, i - nameStart, CodeTokenKind.Tag));
            while (i < code.Length && code[i] != '>')
            {
                if (code[i] is '"' or '\'')
                {
                    var quote = code[i];
                    var end = code.IndexOf(quote, i + 1);
                    end = end < 0 ? code.Length : end + 1;
                    tokens.Add(new(i, end - i, CodeTokenKind.String));
                    i = end;
                    continue;
                }
                if (char.IsLetter(code[i]) || code[i] == '_')
                {
                    var start = i;
                    while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] is ':' or '.' or '-' or '_')) i++;
                    tokens.Add(new(start, i - start, CodeTokenKind.Attribute));
                    continue;
                }
                i++;
            }
            if (i < code.Length) { tokens.Add(new(i - (i > 0 && code[i - 1] is '/' or '?' ? 1 : 0), i > 0 && code[i - 1] is '/' or '?' ? 2 : 1, CodeTokenKind.Tag)); i++; }
        }
        return tokens;
    }

    private static bool Starts(string text, int index, string value) =>
        value.Length > 0 && index + value.Length <= text.Length && string.CompareOrdinal(text, index, value, 0, value.Length) == 0;

    private static int LineEnd(string text, int index)
    {
        var end = text.IndexOf('\n', index);
        return end < 0 ? text.Length : end;
    }
}

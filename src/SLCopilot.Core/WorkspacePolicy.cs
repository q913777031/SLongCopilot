using System.Text.RegularExpressions;

namespace SLCopilot.Core;

/// <summary>A conservative project boundary, not an OS sandbox against concurrent hostile local processes.</summary>
public sealed class WorkspacePolicy
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".vs", "bin", "obj", "node_modules", ".idea", ".ssh", ".aws", ".azure", ".venv", "venv", "__pycache__", "packages", "TestResults" };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".cs", ".csproj", ".sln", ".slnx", ".xaml", ".xml", ".json", ".md", ".txt", ".yml", ".yaml", ".props", ".targets",
      ".config", ".toml", ".razor", ".cshtml", ".resx", ".vb", ".vbproj", ".fs", ".fsproj", ".ts", ".tsx", ".js", ".jsx",
      ".mjs", ".cjs", ".html", ".css", ".scss", ".less", ".sql", ".py", ".cpp", ".c", ".h", ".hpp", ".java", ".kt",
      ".go", ".rs", ".rb", ".php", ".sh", ".ps1", ".bat", ".cmd", ".vue", ".svelte", ".proto", ".graphql", ".ini", ".csv" };
    private readonly List<Regex> ignores = [];
    public string Root { get; }

    public WorkspacePolicy(string root)
    {
        if (!Compat.IsPathFullyQualified(root) || root.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("Select an absolute local project directory.");
        Root = Compat.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(Root) || Root == Path.GetPathRoot(Root) ||
            string.Equals(Root, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select a project directory, not a drive or home directory.");
        CheckLinks(Root);
        foreach (var name in new[] { ".gitignore", ".slcopilotignore" })
        {
            var file = Path.Combine(Root, name);
            if (!File.Exists(file)) continue;
            CheckLinks(file);
            if (new FileInfo(file).Length > 32768) throw new WorkspaceException("Ignore file is too large.");
            foreach (var raw in File.ReadAllLines(file))
            {
                var rule = raw.Trim();
                // Conservative root-level subset: exclusion only. Negation never expands access.
                if (rule.Length == 0 || rule.StartsWith("#", StringComparison.Ordinal) || rule.StartsWith("!", StringComparison.Ordinal)) continue;
                var anchored = rule.StartsWith("/", StringComparison.Ordinal);
                rule = rule.Trim('/').Replace('\\', '/');
                if (rule.Length == 0) continue;
                ignores.Add(new Regex((anchored || rule.Contains('/') ? "^" : "(^|/)") + GlobExpression(rule) + "($|/)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)));
            }
        }
    }

    /// <summary>把以 / 分隔的通配模式转为正则表达式主体（不含锚定）：* 与 ? 不跨目录，** 匹配任意层目录。</summary>
    internal static string GlobExpression(string pattern) =>
        Regex.Escape(pattern).Replace("\\*\\*/", "\u0002").Replace("\\*\\*", "\u0001")
            .Replace("\\*", "[^/]*").Replace("\\?", "[^/]")
            .Replace("\u0001", ".*").Replace("\u0002", "(?:.*/)?");

    public string Resolve(string relative, bool directory = false)
    {
        if (Validate(relative, out var full) is { } error) throw new WorkspaceException(error);
        CheckLinks(full);
        if (!directory)
        {
            if (full == Root || Directory.Exists(full)) throw new WorkspaceException("Expected a file path.");
            if (!SupportedFile(full)) throw new WorkspaceException("Only supported source/text files can be accessed.");
        }
        return full;
    }

    /// <summary>
    /// 遍历目录（search_files / find_files）时判断一个子项是否可访问：名称、排除与忽略规则、扩展名与 <see cref="Resolve"/> 相同；
    /// 链接只看该项自身的属性（来自目录枚举，不另行访问文件系统）。调用方必须从经 <see cref="Resolve"/> 校验的目录开始、只进入本方法允许的子目录，
    /// 因此上级目录都已确认不是链接，不再逐级检查（逐级检查使每个文件多出与目录深度相同次数的文件系统调用）。
    /// </summary>
    internal bool AllowsWalkEntry(string relative, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0 || Validate(relative, out var full) is not null) return false;
        return (attributes & FileAttributes.Directory) != 0 || SupportedFile(full);
    }

    /// <summary>纯路径检查（不访问文件系统）：不合法时返回原因，合法时 <paramref name="full"/> 为完整路径。</summary>
    private string? Validate(string relative, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0)
            return "Use a relative path inside the selected workspace.";
        var normalized = relative.Replace('\\', '/');
        var parts = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToArray();
        foreach (var part in parts)
        {
            if (part == ".." || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) || part.Any(c => c < 32 || "<>\"|?*".IndexOf(c) >= 0) ||
                ReservedName.IsMatch(part.Split('.')[0]))
                return "Invalid or unsafe path.";
            if (Excluded.Contains(part) || part.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
                part.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0 || part.IndexOf("credential", StringComparison.OrdinalIgnoreCase) >= 0 ||
                part.Equals(".slcopilotignore", StringComparison.OrdinalIgnoreCase))
                return "Path is excluded by workspace policy.";
        }
        normalized = string.Join("/", parts);
        if (ignores.Any(r => r.IsMatch(normalized))) return "Path is excluded by ignore rules.";
        full = Path.GetFullPath(Path.Combine(Root, normalized));
        var comparison = Compat.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (full != Root && !full.StartsWith(Root + Path.DirectorySeparatorChar, comparison)) return "Path is outside the workspace.";
        return null;
    }

    private static bool SupportedFile(string full) =>
        Extensions.Contains(Path.GetExtension(full)) || Path.GetFileName(full) is ".gitignore" or ".editorconfig" or "Dockerfile" or "Makefile";

    private static readonly Regex ReservedName = new("^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static void CheckLinks(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new WorkspaceException("Symbolic links and directory junctions are not allowed.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}

using System.Text.RegularExpressions;

namespace SLCopilot.Core;

/// <summary>只对部分文件生效的指令：<see cref="RelativePath"/> 为指令文件，<see cref="ApplyTo"/> 为通配模式（原文）。</summary>
public sealed record ScopedInstruction(string RelativePath, IReadOnlyList<string> ApplyTo, string Text)
{
    internal IReadOnlyList<(Regex Regex, bool NameOnly)> Matchers { get; init; } = [];

    /// <summary>相对路径是否匹配任一 applyTo 模式（不含 / 的模式匹配文件名，含 / 的模式匹配相对工作区的路径）。</summary>
    public bool Matches(string relative)
    {
        var path = relative.Replace('\\', '/').TrimStart('/');
        var name = path.Substring(path.LastIndexOf('/') + 1);
        return Matchers.Any(m => m.Regex.IsMatch(m.NameOnly ? name : path));
    }
}

/// <summary>
/// 按路径生效的指令（兼容 Copilot）：<c>.slcopilot/instructions/*.md</c> 与 <c>.github/instructions/*.instructions.md</c>，
/// 开头 front matter 的 <c>applyTo:</c> 为逗号分隔的通配模式（如 <c>src/FrameworkWorkflow/**, **/*.xaml</c>）。没有 applyTo 的文件不自动生效。
/// 文件受工作区策略约束；读取失败、过大或格式不对的文件跳过（指令是可选的）。
/// </summary>
public static class ScopedInstructionLoader
{
    public const int MaxFiles = 50, MaxChars = 8_000;
    private const int MaxBytes = 65_536;
    private static readonly (string Folder, string Suffix)[] Sources = [(".slcopilot/instructions", ".md"), (".github/instructions", ".instructions.md")];

    public static IReadOnlyList<ScopedInstruction> Load(string root)
    {
        var result = new List<ScopedInstruction>();
        WorkspacePolicy policy;
        try { policy = new WorkspacePolicy(root); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return result; }
        foreach (var (folder, suffix) in Sources)
        {
            string directory;
            try { directory = policy.Resolve(folder, directory: true); }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { continue; }
            if (!Directory.Exists(directory)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(directory, "*" + suffix).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                if (result.Count >= MaxFiles) return result;
                var relative = folder + "/" + Path.GetFileName(file);
                try
                {
                    var full = policy.Resolve(relative);
                    if (new FileInfo(full).Length > MaxBytes) continue;
                    if (Parse(relative, new TextCodec(null).Decode(File.ReadAllBytes(full)).Text) is { } instruction) result.Add(instruction);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        }
        return result;
    }

    /// <summary>解析一个指令文件；没有 applyTo 或正文为空时返回 null。</summary>
    public static ScopedInstruction? Parse(string relativePath, string text)
    {
        var body = text.Replace("\r\n", "\n").Trim();
        if (!body.StartsWith("---\n", StringComparison.Ordinal) || body.IndexOf("\n---", 3, StringComparison.Ordinal) is not (var end and > 0)) return null;
        var globs = new List<string>();
        foreach (var line in body.Substring(4, end - 4).Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0 || !string.Equals(line.Substring(0, colon).Trim(), "applyTo", StringComparison.OrdinalIgnoreCase)) continue;
            globs.AddRange(line.Substring(colon + 1).Split(',').Select(g => g.Trim().Trim('"', '\'').Trim()).Where(g => g.Length > 0));
        }
        var after = body.IndexOf('\n', end + 1);
        body = after < 0 ? "" : body.Substring(after + 1).Trim();
        if (globs.Count == 0 || body.Length == 0) return null;
        if (body.Length > MaxChars) body = body.Substring(0, MaxChars) + "\n[Instructions truncated]";
        var matchers = new List<(Regex, bool)>();
        foreach (var glob in globs)
        {
            var pattern = glob.Replace('\\', '/').TrimStart('/');
            if (pattern.Length == 0) continue;
            // 不含 / 的模式匹配文件名，含 / 的匹配相对路径。
            matchers.Add((new Regex("^" + WorkspacePolicy.GlobExpression(pattern) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)),
                pattern.IndexOf('/') < 0));
        }
        return new ScopedInstruction(relativePath, globs, body) { Matchers = matchers };
    }
}

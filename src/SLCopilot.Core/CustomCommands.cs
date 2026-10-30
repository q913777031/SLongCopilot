using System.Text.RegularExpressions;

namespace SLCopilot.Core;

/// <summary>
/// 项目自定义斜杠命令：<c>.slcopilot/commands/名称.md</c>，兼容 Copilot 的 <c>.github/prompts/名称.prompt.md</c>。
/// 文件内容即提示模板（可用 <c>$ARGUMENTS</c>）；可选的开头 front matter（<c>---</c> 之间）支持 <c>description:</c> 与 <c>mode:</c>（ask / agent / plan，Copilot 的 edit 视为 agent）。
/// 文件受工作区策略约束；读取失败、过大或名称不合法的文件跳过，不阻断请求。
/// </summary>
public static class CustomCommands
{
    public const int MaxCommands = 50;
    private const int MaxBytes = 65_536;
    private static readonly (string Directory, string Suffix)[] Locations = [(".slcopilot/commands", ".md"), (".github/prompts", ".prompt.md")];
    private static readonly Regex ValidName = new("^" + PromptCommands.NamePattern + "$", RegexOptions.CultureInvariant);

    /// <summary>读取工作区中的自定义命令，按名称排序；同名时 .slcopilot 优先，与内置命令同名的跳过。</summary>
    public static IReadOnlyList<PromptCommand> Load(string root)
    {
        WorkspacePolicy policy;
        try { policy = new WorkspacePolicy(root); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return []; }
        var result = new List<PromptCommand>();
        foreach (var (directory, suffix) in Locations)
        {
            IEnumerable<string> files;
            try
            {
                var full = policy.Resolve(directory, directory: true);
                if (!Directory.Exists(full)) continue;
                files = Directory.EnumerateFiles(full, "*" + suffix).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
            foreach (var file in files)
            {
                if (result.Count >= MaxCommands) break;
                var fileName = Path.GetFileName(file);
                if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                var name = fileName.Substring(0, fileName.Length - suffix.Length);
                if (!ValidName.IsMatch(name) || PromptCommands.Find(name, result) is not null) continue;
                var relative = directory + "/" + fileName;
                try
                {
                    var full = policy.Resolve(relative);
                    if (new FileInfo(full).Length > MaxBytes) continue;
                    if (Parse(name, relative, new TextCodec(null).Decode(File.ReadAllBytes(full)).Text) is { } command) result.Add(command);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        }
        return result.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>解析单个命令文件；模板为空时返回 null。</summary>
    public static PromptCommand? Parse(string name, string source, string text)
    {
        var body = text.Replace("\r\n", "\n").Trim();
        string? description = null;
        RunMode? mode = null;
        if (body.StartsWith("---\n", StringComparison.Ordinal) && body.IndexOf("\n---", 3, StringComparison.Ordinal) is var end and > 0)
        {
            foreach (var line in body.Substring(4, end - 4).Split('\n'))
            {
                var colon = line.IndexOf(':');
                if (colon < 0) continue;
                var key = line.Substring(0, colon).Trim().ToLowerInvariant();
                var value = line.Substring(colon + 1).Trim().Trim('"', '\'');
                if (key == "description" && value.Length > 0) description = value;
                else if (key == "mode") mode = value.ToLowerInvariant() switch
                {
                    "ask" => RunMode.Ask,
                    "agent" or "edit" => RunMode.Agent,
                    "plan" => RunMode.Plan,
                    _ => null,
                };
            }
            var after = body.IndexOf('\n', end + 1);
            body = after < 0 ? "" : body.Substring(after + 1).Trim();
        }
        if (body.Length == 0) return null;
        description ??= body.Split('\n').First(l => l.Trim().Length > 0).Trim().TrimStart('#').Trim();
        if (description.Length > 60) description = description.Substring(0, 60) + "…";
        return new(name.ToLowerInvariant(), description + "（" + source + "）", body, mode, source);
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;

namespace SLCopilot.Core;

/// <summary>
/// 写入保护规则（<c>.slcopilot/settings.json</c> 的 <c>protect</c> 数组，通配模式同 find_files：不含 / 时匹配任意层的文件名，含 / 时匹配相对工作区的路径，** 匹配任意层）。
/// 匹配的路径不能被智能体写入、删除或移动（自动批准开启时同样生效）。设置文件本身始终受保护。
/// 设置文件存在但无法读取或格式错误时保护全部路径（失效即安全），并在 <see cref="LoadError"/> 中说明原因。
/// </summary>
public sealed class WriteProtection
{
    public const string SettingsPath = ".slcopilot/settings.json";
    private const int MaxSettingsBytes = 65_536;
    private readonly List<(string Pattern, Regex Regex)> rules = [];

    private WriteProtection() { }

    /// <summary>配置的保护模式（原文）。</summary>
    public IReadOnlyList<string> Patterns => rules.Select(r => r.Pattern).ToList();
    /// <summary>设置文件无法读取或格式错误：本次保护全部路径。</summary>
    public bool ProtectsAll { get; private set; }
    public string? LoadError { get; private set; }

    /// <summary>读取工作区的保护规则；设置文件不存在时没有规则（只保护设置文件本身）。</summary>
    public static WriteProtection Load(string root)
    {
        var protection = new WriteProtection();
        var file = Path.Combine(root, ".slcopilot", "settings.json");
        try
        {
            if (!File.Exists(file)) return protection;
            if (new FileInfo(file).Length > MaxSettingsBytes) throw new InvalidDataException($"{SettingsPath} is larger than 64 KiB.");
            using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{SettingsPath} must contain a JSON object.");
            if (!document.RootElement.TryGetProperty("protect", out var protect)) return protection;
            if (protect.ValueKind != JsonValueKind.Array) throw new InvalidDataException("\"protect\" must be an array of path patterns.");
            foreach (var item in protect.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } raw || raw.Trim().Length == 0)
                    throw new InvalidDataException("Each \"protect\" entry must be a nonempty string.");
                protection.rules.Add((raw.Trim(), Compile(raw)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            protection.rules.Clear();
            protection.ProtectsAll = true;
            protection.LoadError = ex.Message;
        }
        return protection;
    }

    private static Regex Compile(string raw)
    {
        var pattern = raw.Trim().Replace('\\', '/');
        while (pattern.StartsWith("./", StringComparison.Ordinal)) pattern = pattern.Substring(2);
        pattern = pattern.TrimStart('/');
        // 以 / 结尾表示整个目录。
        if (pattern.EndsWith("/", StringComparison.Ordinal)) pattern += "**";
        if (pattern.Length == 0) throw new InvalidDataException("A \"protect\" entry is empty.");
        var body = WorkspacePolicy.GlobExpression(pattern);
        // 不含 / 的模式匹配任意层的文件或目录名（及其下的全部内容）；含 / 的模式从工作区根开始匹配（目录模式同样覆盖其下内容）。
        var expression = pattern.IndexOf('/') < 0 ? "(^|/)" + body + "($|/)" : "^" + body + "($|/)";
        return new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    /// <summary>相对路径（/ 或 \ 分隔）受保护时返回原因（英文，交给模型），否则返回 null。</summary>
    public string? Check(string relative)
    {
        var path = relative.Replace('\\', '/').TrimStart('/');
        if (string.Equals(path, SettingsPath, StringComparison.OrdinalIgnoreCase))
            return $"{SettingsPath} holds the user's protection rules and cannot be changed by the agent.";
        if (ProtectsAll)
            return $"All writes are blocked because {SettingsPath} could not be read ({LoadError}). The user must fix that file.";
        foreach (var (pattern, regex) in rules)
            if (regex.IsMatch(path))
                return $"{path} is write-protected by the rule \"{pattern}\" in {SettingsPath}.";
        return null;
    }
}

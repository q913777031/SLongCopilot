using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SLCopilot.Core;

/// <summary>
/// 一个 MCP 服务器的配置。<see cref="Command"/> 非空时为 stdio 服务器（程序名或完整路径，不经过 shell），否则 <see cref="Url"/> 为 Streamable HTTP 端点。
/// <see cref="Env"/>、<see cref="Headers"/> 与 <see cref="Args"/> 中的 <c>${env:NAME}</c> 在启动时替换为环境变量（密钥不写入配置文件）。
/// </summary>
public sealed record McpServerConfig(string Name, string? Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env, string? Cwd,
    string? Url, IReadOnlyDictionary<string, string> Headers, int TimeoutSeconds, string Source)
{
    public bool IsHttp => Command is null;

    /// <summary>展示给用户的一行说明：stdio 为命令行，HTTP 为地址。</summary>
    public string Describe() => IsHttp ? Url! : Command + (Args.Count > 0 ? " " + string.Join(" ", Args.Select(CommandRunner.Quote)) : "");
}

/// <summary>一个配置文件：路径、是否来自工作区（需用户信任）、内容哈希与其中的服务器。</summary>
public sealed record McpConfigFile(string Path, bool FromWorkspace, string Hash, IReadOnlyList<McpServerConfig> Servers);

/// <summary>
/// 读取 MCP 配置。格式兼容 VS Code / Visual Studio（<c>servers</c>）与 Claude Code（<c>mcpServers</c>）：
/// <code>{ "servers": { "名称": { "command": "...", "args": [...], "env": {...}, "cwd": "..." }, "名称2": { "type": "http", "url": "https://...", "headers": {...} } } }</code>
/// 服务器名称只能含字母、数字、_ 与 -（最长 20），最多 10 个。HTTP 地址必须是 https（本机回环地址除外）。格式错误抛出 ArgumentException。
/// </summary>
public static class McpConfigLoader
{
    public const int MaxServers = 10, MaxFileBytes = 64 * 1024, DefaultTimeoutSeconds = 120, MaxTimeoutSeconds = 1800;
    /// <summary>工作区中依次查找的配置文件（找到第一个即停止）。</summary>
    public static readonly string[] WorkspaceFiles = [Path.Combine(".slcopilot", "mcp.json"), ".mcp.json"];
    private static readonly Regex ServerName = new("^[A-Za-z0-9_-]{1,20}$", RegexOptions.CultureInvariant);
    private static readonly Regex EnvReference = new("\\$\\{env:([A-Za-z_][A-Za-z0-9_]*)\\}", RegexOptions.CultureInvariant);

    /// <summary>读取工作区配置（<see cref="WorkspaceFiles"/> 中第一个存在的文件）；没有时返回 null。符号链接与超过 64 KiB 的文件拒绝。</summary>
    public static McpConfigFile? LoadWorkspace(string root)
    {
        foreach (var relative in WorkspaceFiles)
        {
            var path = Path.Combine(root, relative);
            if (File.Exists(path)) return Load(path, fromWorkspace: true);
        }
        return null;
    }

    /// <summary>读取一个配置文件；文件不存在时返回 null。</summary>
    public static McpConfigFile? Load(string path, bool fromWorkspace)
    {
        if (!File.Exists(path)) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException($"{path} is a symbolic link; MCP configuration must be a regular file.");
        if (new FileInfo(path).Length > MaxFileBytes) throw new ArgumentException($"{path} is larger than 64 KiB.");
        var bytes = File.ReadAllBytes(path);
        return Parse(new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'), path, fromWorkspace, Compat.Sha256Hex(bytes));
    }

    public static McpConfigFile Parse(string json, string path, bool fromWorkspace, string hash)
    {
        JsonObject root;
        try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new ArgumentException($"{path}: the root must be a JSON object."); }
        catch (JsonException ex) { throw new ArgumentException($"{path}: invalid JSON ({ex.Message})."); }
        var servers = (root["servers"] ?? root["mcpServers"]) as JsonObject
            ?? throw new ArgumentException($"{path}: expected a \"servers\" (or \"mcpServers\") object.");
        if (servers.Count > MaxServers) throw new ArgumentException($"{path}: at most {MaxServers} servers are supported.");
        var result = new List<McpServerConfig>();
        foreach (var pair in servers)
        {
            if (!ServerName.IsMatch(pair.Key)) throw new ArgumentException($"{path}: server name \"{pair.Key}\" may contain only letters, digits, _ and - (at most 20).");
            if (pair.Value is not JsonObject server) throw new ArgumentException($"{path}: server \"{pair.Key}\" must be an object.");
            var type = String(server, "type", pair.Key, path);
            var command = String(server, "command", pair.Key, path);
            var url = String(server, "url", pair.Key, path);
            if (server["disabled"] is JsonValue disabled && disabled.TryGetValue<bool>(out var off) && off) continue;
            if ((command is null) == (url is null)) throw new ArgumentException($"{path}: server \"{pair.Key}\" needs exactly one of \"command\" (stdio) or \"url\" (http).");
            if (type is not (null or "stdio" or "http" or "streamable-http")) throw new ArgumentException($"{path}: server \"{pair.Key}\" has unsupported type \"{type}\"; use stdio or http.");
            if (url is not null)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
                    throw new ArgumentException($"{path}: server \"{pair.Key}\" url must be https (http only for localhost).");
            }
            var timeout = server["timeout_seconds"] is JsonValue t && t.TryGetValue<int>(out var seconds) ? seconds : DefaultTimeoutSeconds;
            if (timeout is < 1 or > MaxTimeoutSeconds) throw new ArgumentException($"{path}: server \"{pair.Key}\" timeout_seconds must be between 1 and {MaxTimeoutSeconds}.");
            result.Add(new(pair.Key, command, Strings(server, "args", pair.Key, path), Map(server, "env", pair.Key, path), String(server, "cwd", pair.Key, path),
                url, Map(server, "headers", pair.Key, path), timeout, path));
        }
        return new(path, fromWorkspace, hash, result);
    }

    /// <summary>信任确认用的说明：文件路径与每个服务器的命令或地址、环境变量名与请求头名（不显示值）。</summary>
    public static string Describe(McpConfigFile file)
    {
        var text = new StringBuilder($"配置文件：{file.Path}\n");
        foreach (var server in file.Servers)
        {
            text.Append($"\n• {server.Name}（{(server.IsHttp ? "HTTP" : "stdio")}）：{server.Describe()}");
            if (server.Cwd is not null) text.Append($"\n  目录：{server.Cwd}");
            if (server.Env.Count > 0) text.Append($"\n  环境变量：{string.Join("、", server.Env.Keys)}");
            if (server.Headers.Count > 0) text.Append($"\n  请求头：{string.Join("、", server.Headers.Keys)}");
        }
        return text.ToString();
    }

    /// <summary>把 <c>${env:NAME}</c> 替换为当前进程的环境变量；变量不存在时抛出 ArgumentException（不以空值启动）。</summary>
    public static string Expand(string value) => EnvReference.Replace(value, match =>
        Environment.GetEnvironmentVariable(match.Groups[1].Value) is { } found ? found
            : throw new ArgumentException($"Environment variable {match.Groups[1].Value} is not set."));

    private static string? String(JsonObject server, string name, string serverName, string path) => server[name] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
        _ => throw new ArgumentException($"{path}: \"{name}\" of server \"{serverName}\" must be a string."),
    };

    private static IReadOnlyList<string> Strings(JsonObject server, string name, string serverName, string path)
    {
        if (server[name] is null) return [];
        if (server[name] is not JsonArray array || array.Any(item => item is not JsonValue v || !v.TryGetValue<string>(out _)))
            throw new ArgumentException($"{path}: \"{name}\" of server \"{serverName}\" must be an array of strings.");
        return array.Select(item => item!.GetValue<string>()).ToList();
    }

    private static IReadOnlyDictionary<string, string> Map(JsonObject server, string name, string serverName, string path)
    {
        if (server[name] is null) return new Dictionary<string, string>();
        if (server[name] is not JsonObject map || map.Any(p => p.Value is not JsonValue v || !v.TryGetValue<string>(out _)))
            throw new ArgumentException($"{path}: \"{name}\" of server \"{serverName}\" must be an object of strings.");
        return map.ToDictionary(p => p.Key, p => p.Value!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 已信任的工作区 MCP 配置（工作区根目录 → 配置文件哈希）。配置内容变化后哈希不同，需要重新确认。
/// 写入时先写临时文件再替换；文件损坏时视为没有任何信任记录。
/// </summary>
public sealed class McpTrustStore(string path)
{
    private readonly object gate = new();

    public bool IsTrusted(string root, string hash)
    {
        lock (gate) return Read().TryGetValue(Key(root), out var trusted) && trusted == hash;
    }

    public void Trust(string root, string hash)
    {
        lock (gate)
        {
            var map = Read();
            map[Key(root)] = hash;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            Compat.MoveOverwrite(temp, path);
        }
    }

    private Dictionary<string, string> Read()
    {
        try
        {
            if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
            return new(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            RunLog.Warn("mcp", "trust_store_unreadable", ("message", ex.Message));
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string Key(string root) => Compat.TrimEndingDirectorySeparator(Path.GetFullPath(root));
}

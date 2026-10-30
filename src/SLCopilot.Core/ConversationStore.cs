using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SLCopilot.Core;

/// <summary>气泡内的一个片段：markdown、code、activity 或 error。</summary>
public sealed record ConversationPart(string Kind, string Text);

/// <summary>一条显示用消息。<see cref="Text"/> 为用户原文或流式期间的纯文本；<see cref="Parts"/> 为回答的渲染片段。</summary>
public sealed record ConversationEntry(string Role, string Header, string Text, ConversationPart[] Parts);

public sealed record ConversationSummary(string Id, string Title, DateTimeOffset UpdatedAt);

/// <summary>
/// 一次完整对话的持久化形式：显示用消息、纯聊天历史、Agent 的原始消息历史（含 thinking 块，供原样回传）。
/// Version 用于日后迁移；读取到更高版本时拒绝。
/// </summary>
public sealed record ConversationRecord
{
    public const int CurrentVersion = 1;
    public int Version { get; init; } = CurrentVersion;
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string ProfileId { get; init; } = "";
    public ConversationEntry[] Entries { get; init; } = [];
    public ChatMessage[] ChatHistory { get; init; } = [];
    public JsonArray? AgentMessages { get; init; }
    public string AgentScope { get; init; } = "";
    public bool AgentInterrupted { get; init; }

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>由第一条用户消息生成标题：取首行，最多 40 个字符。</summary>
    public static string TitleFrom(string firstUserText)
    {
        var line = (firstUserText ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length <= 40 ? line : line.Substring(0, 40) + "…";
    }
}

/// <summary>
/// 把对话保存为 <c>{目录}/{Id}.json</c>。写入采用同目录临时文件 + 替换；最多保留 <see cref="MaxConversations"/> 个，超出时删除最旧的。
/// 损坏或版本过高的文件在列表中跳过，不抛出；只有当前对话 Id 格式非法时拒绝写入。
/// </summary>
public sealed class ConversationStore(string directory)
{
    public const int MaxConversations = 100;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public string Directory { get; } = Path.GetFullPath(directory);

    public IReadOnlyList<ConversationSummary> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var summaries = new List<ConversationSummary>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            var record = TryRead(file);
            if (record is not null) summaries.Add(new(record.Id, record.Title, record.UpdatedAt));
        }
        return summaries.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    public ConversationRecord? Load(string id)
    {
        ValidateId(id);
        var file = PathFor(id);
        return File.Exists(file) ? TryRead(file) : null;
    }

    public void Save(ConversationRecord record)
    {
        ValidateId(record.Id);
        if (record.Version != ConversationRecord.CurrentVersion) throw new ArgumentException("Unsupported conversation version.");
        System.IO.Directory.CreateDirectory(Directory);
        var file = PathFor(record.Id);
        var temp = file + ".tmp";
        File.WriteAllBytes(temp, new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(record, Options)));
        Compat.MoveOverwrite(temp, file);
        Prune(record.Id);
    }

    public void Delete(string id)
    {
        ValidateId(id);
        var file = PathFor(id);
        if (File.Exists(file)) File.Delete(file);
    }

    private void Prune(string keepId)
    {
        var files = System.IO.Directory.GetFiles(Directory, "*.json")
            .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        foreach (var stale in files.Skip(MaxConversations))
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(stale.Name), keepId, StringComparison.OrdinalIgnoreCase)) continue;
            try { stale.Delete(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private string PathFor(string id) => Path.Combine(Directory, id + ".json");

    private static void ValidateId(string id)
    {
        if (id.Length != 32 || id.Any(c => !(char.IsDigit(c) || c is >= 'a' and <= 'f')))
            throw new ArgumentException("Conversation id must be a 32-character lowercase hex string.");
    }

    private static ConversationRecord? TryRead(string file)
    {
        try
        {
            if (new FileInfo(file).Length > 50_000_000) return null;
            var record = JsonSerializer.Deserialize<ConversationRecord>(File.ReadAllText(file), Options);
            if (record is null || record.Version > ConversationRecord.CurrentVersion || record.Id.Length != 32) return null;
            return record;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

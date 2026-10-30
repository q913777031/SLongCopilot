using System.Text.Json.Nodes;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class ConversationStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SLCopilot-conv-" + Guid.NewGuid().ToString("N"));
    private ConversationStore Store => new(directory);

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private static ConversationRecord Record(string id, string title, DateTimeOffset updated) => new()
    {
        Id = id, Title = title, CreatedAt = updated, UpdatedAt = updated, ProfileId = "openai",
        Entries = [new("user", "你", "hello", []), new("assistant", "SLCopilot", "", [new("markdown", "**hi**"), new("activity", "▸ read_file A.cs")])],
        ChatHistory = [new("user", "hello"), new("assistant", "**hi**")],
        AgentMessages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" },
            new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "sig" }) }),
        AgentScope = "scope", AgentInterrupted = true,
    };

    [Fact]
    public void SaveLoadListAndDeleteRoundTrip()
    {
        var id = ConversationRecord.NewId();
        var record = Record(id, "第一个", DateTimeOffset.UtcNow);
        Store.Save(record);
        var loaded = Store.Load(id)!;
        Assert.Equal(record.Title, loaded.Title);
        Assert.Equal(2, loaded.Entries.Length);
        Assert.Equal("▸ read_file A.cs", loaded.Entries[1].Parts[1].Text);
        Assert.Equal("sig", loaded.AgentMessages![1]!["content"]![0]!["signature"]!.GetValue<string>());
        Assert.True(loaded.AgentInterrupted);
        Assert.Equal([id], Store.List().Select(s => s.Id));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Store.Delete(id);
        Assert.Null(Store.Load(id));
        Assert.Empty(Store.List());
    }

    [Fact]
    public void ListIsNewestFirstAndSkipsCorruptFiles()
    {
        var older = Record(ConversationRecord.NewId(), "old", DateTimeOffset.UtcNow.AddHours(-1));
        var newer = Record(ConversationRecord.NewId(), "new", DateTimeOffset.UtcNow);
        Store.Save(older); Store.Save(newer);
        System.IO.File.WriteAllText(Path.Combine(directory, new string('a', 32) + ".json"), "{ not json");
        System.IO.File.WriteAllText(Path.Combine(directory, new string('b', 32) + ".json"), "{\"Version\":99,\"Id\":\"" + new string('b', 32) + "\"}");
        Assert.Equal(["new", "old"], Store.List().Select(s => s.Title));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("short")]
    public void UnsafeIdsAreRejected(string id)
    {
        Assert.Throws<ArgumentException>(() => Store.Save(Record(id, "x", DateTimeOffset.UtcNow)));
        Assert.Throws<ArgumentException>(() => Store.Load(id));
    }

    [Fact]
    public void OldestConversationsArePruned()
    {
        for (var i = 0; i <= ConversationStore.MaxConversations; i++)
        {
            Store.Save(Record(ConversationRecord.NewId(), "c" + i, DateTimeOffset.UtcNow.AddMinutes(i)));
            System.IO.File.SetLastWriteTimeUtc(Directory.GetFiles(directory, "*.json").OrderBy(System.IO.File.GetLastWriteTimeUtc).Last(), DateTime.UtcNow.AddMinutes(i));
        }
        Assert.Equal(ConversationStore.MaxConversations, Directory.GetFiles(directory, "*.json").Length);
    }

    [Fact]
    public void TitleUsesFirstNonEmptyLineTruncated()
    {
        Assert.Equal("分析代码", ConversationRecord.TitleFrom("\n  分析代码\n第二行"));
        Assert.Equal(new string('x', 40) + "…", ConversationRecord.TitleFrom(new string('x', 50)));
    }
}

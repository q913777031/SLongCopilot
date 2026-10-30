using System.Text.Json.Nodes;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

/// <summary>检查点恢复时对话回退到运行之前。</summary>
public sealed class AgentConversationTests
{
    [Fact]
    public void RestoreToReplacesHistoryAndKeepsTheSameObject()
    {
        var conversation = new AgentConversation(new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "a" }), interrupted: false);
        var before = conversation.Export();
        var later = new AgentConversation(new JsonArray(
            new JsonObject { ["role"] = "user", ["content"] = "a" }, new JsonObject { ["role"] = "assistant", ["content"] = "b" }), interrupted: true);
        conversation.RestoreTo(later.Export(), true);
        Assert.True(conversation.Interrupted);
        Assert.Equal(2, conversation.Export().Count);

        conversation.RestoreTo(before, false);
        Assert.False(conversation.Interrupted);
        Assert.Single(conversation.Export());
        Assert.Equal(0, conversation.LastContextTokens);
        before.Add(new JsonObject { ["role"] = "user", ["content"] = "mutated" });
        Assert.Single(conversation.Export());
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

/// <summary>节约 token：OpenAI 发送时省略较早工具结果。</summary>
public sealed class TokenSavingTests
{
    [Fact]
    public void OnlyOlderLongToolResultsAreTrimmed()
    {
        var messages = new JsonArray();
        for (var i = 0; i < 5; i++)
            messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = "t" + i, ["content"] = new string('x', 3000) });
        messages.Insert(0, new JsonObject { ["role"] = "tool", ["tool_call_id"] = "short", ["content"] = "ok" });
        AgentProtocol.TrimOldToolResults(messages);
        var contents = messages.Select(m => (string)m!["content"]!).ToList();
        Assert.Equal("ok", contents[0]);
        Assert.StartsWith("[Earlier tool result omitted", contents[1]);
        Assert.StartsWith("[Earlier tool result omitted", contents[2]);
        Assert.All(contents.Skip(3), c => Assert.Equal(3000, c.Length));
    }

    [Fact]
    public async Task TrimmingChangesOnlyTheWireCopy()
    {
        var handler = new Capture("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"Done\"}}]}");
        using var http = new HttpClient(handler);
        var history = new JsonArray();
        for (var i = 0; i < 5; i++)
            history.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = "t" + i, ["content"] = new string('x', 3000) });
        await new AgentProtocol(http).CompleteAsync(new ProviderSettings(), "key", "system", history, null, null, CancellationToken.None, trimOldToolResults: true);
        Assert.All(history, m => Assert.Equal(3000, ((string)m!["content"]!).Length));
        var sent = handler.Requests[0]["messages"]!.AsArray();
        Assert.StartsWith("[Earlier tool result omitted", (string)sent[1]!["content"]!);
    }

    private sealed class Capture(string response) : HttpMessageHandler
    {
        public List<JsonNode> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
            return new(HttpStatusCode.OK) { Content = new StringContent(Sse.Convert(response), Encoding.UTF8, "text/event-stream") };
        }
    }
}

public sealed class EditMatchingTests
{
    [Fact]
    public void LocateEditExplainsMissingAndAmbiguousText()
    {
        Assert.Equal((1, 5), WorkspaceTools.LocateEdit("a\r\nb\r\nc", "\nb\n"));
        Assert.Equal((0, 4), WorkspaceTools.LocateEdit("x\ny\n", "x\ny\n"[..4]));
        Assert.Contains("not found", Assert.Throws<WorkspaceException>(() => WorkspaceTools.LocateEdit("abc", "zz")).Message);
        Assert.Contains("occurs 2 times", Assert.Throws<WorkspaceException>(() => WorkspaceTools.LocateEdit("x;x;", "x;")).Message);
        Assert.Contains("occurs 2 times", Assert.Throws<WorkspaceException>(() => WorkspaceTools.LocateEdit("a\r\nb\r\na\r\nb", "a\nb")).Message);
    }

    [Fact]
    public void NewTextFollowsTheFilesDominantLineEnding()
    {
        Assert.Equal("1\r\n2", WorkspaceTools.MatchLineEndings("1\n2", "a\r\nb\r\nc\n"));
        Assert.Equal("1\n2", WorkspaceTools.MatchLineEndings("1\r\n2", "a\nb\n"));
        Assert.Equal("1\n2", WorkspaceTools.MatchLineEndings("1\n2", "single line"));
    }
}

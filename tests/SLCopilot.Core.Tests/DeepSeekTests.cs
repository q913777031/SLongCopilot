using System.Net;
using System.Text.Json.Nodes;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class DeepSeekTests
{
    private static ProviderSettings Settings => new() { Provider = ProviderKind.DeepSeek, Endpoint = "https://api.deepseek.com/v1", Model = "deepseek-flash" };

    [Theory]
    [InlineData("", null)]
    [InlineData("none", "disabled")]
    [InlineData("max", "enabled")]
    [InlineData("medium", "enabled")]
    public async Task ChatUsesDeepSeekParameters(string effort, string? thinking)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await foreach (var _ in new AiClient(http).StreamAsync(Settings with { OpenAiReasoningEffort = effort }, "key", "system", [new("user", "hello")], default)) { }
        Assert.Equal(16000, (int?)handler.Body!["max_tokens"]);
        Assert.Null(handler.Body["max_completion_tokens"]);
        Assert.Equal(thinking, (string?)handler.Body["thinking"]?["type"]);
        Assert.Equal(effort == "max" ? "max" : effort == "medium" ? "high" : null, (string?)handler.Body["reasoning_effort"]);
    }

    [Fact]
    public async Task AgentPreservesReasoningAcrossRequests()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var protocol = new AgentProtocol(http);
        var turn = await protocol.CompleteAsync(Settings, "key", "system", new(), default);
        Assert.Equal("ab", (string?)turn.Message["reasoning_content"]);
        Assert.Equal("done", turn.Text);
        await protocol.CompleteAsync(Settings, "key", "system", new JsonArray(turn.Message, new JsonObject { ["role"] = "user", ["content"] = "continue" }), default);
        Assert.Equal("ab", (string?)handler.Body!["messages"]![1]!["reasoning_content"]);
        Assert.NotNull(handler.Body["tools"]);
    }

    [Fact]
    public void ExistingOfficialOpenAiProfileIsRecognized()
    {
        Assert.True((Settings with { Provider = ProviderKind.OpenAI }).IsDeepSeek);
        Assert.False(new ProviderSettings { Endpoint = "https://api.deepseek.com.example.org" }.IsDeepSeek);
        Assert.Equal("https://api.deepseek.com/v1/chat/completions", Settings.RequestUri().ToString());
        Assert.NotNull((Settings with { OpenAiReasoningEffort = "medium" }).RequestUri());
    }

    [Fact]
    public async Task ToolRoundAndSyntheticHistoryRemainValid()
    {
        using var handler = new Handler { ToolResponse = true };
        using var http = new HttpClient(handler);
        var protocol = new AgentProtocol(http);
        var history = new JsonArray(new JsonObject { ["role"] = "assistant", ["content"] = "Summary acknowledged" });
        var turn = await protocol.CompleteAsync(Settings, "key", "system", history, default);
        Assert.Equal("", (string?)handler.Body!["messages"]![1]!["reasoning_content"]);
        Assert.Null(history[0]!["reasoning_content"]);
        Assert.Single(turn.Calls);
        history.Add(turn.Message);
        history.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call1", ["content"] = "file contents" });
        handler.ToolResponse = false;
        await protocol.CompleteAsync(Settings, "key", "system", history, default);
        Assert.Equal("a", (string?)handler.Body!["messages"]![2]!["reasoning_content"]);
        Assert.Equal("call1", (string?)handler.Body["messages"]![3]!["tool_call_id"]);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public JsonNode? Body;
        public bool ToolResponse;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            if (ToolResponse)
                return new(HttpStatusCode.OK) { Content = new StringContent(
                    "data: " + new JsonObject { ["choices"] = new JsonArray(new JsonObject {
                        ["delta"] = new JsonObject { ["reasoning_content"] = "a", ["tool_calls"] = new JsonArray(new JsonObject {
                            ["index"] = 0, ["id"] = "call1", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "read_file", ["arguments"] = "{}" } }) },
                        ["finish_reason"] = "tool_calls" }) }.ToJsonString() + "\n\ndata: [DONE]\n\n") };
            return new(HttpStatusCode.OK) { Content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"a\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"b\",\"content\":\"done\"},\"finish_reason\":\"stop\"}]}\n\n" +
                "data: [DONE]\n\n") };
        }
    }
}

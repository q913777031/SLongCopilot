using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class ModelPickerTests
{
    [Fact]
    public void ClaudeIdsGetShortLabelsAndOthersStayRaw()
    {
        Assert.Equal("Opus 5.5", ModelNames.Label("claude-opus-5-5"));
        Assert.Equal("Fable 5.1", ModelNames.Label("claude-fable-5-1"));
        Assert.Equal("Haiku 4.5", ModelNames.Label("claude-haiku-4-5"));
        Assert.Equal("Opus 4.1", ModelNames.Label("claude-opus-4-1-20250805"));
        Assert.Equal("claude-3-7-sonnet-latest", ModelNames.Label("claude-3-7-sonnet-latest"));
        Assert.Equal("gpt-4.1", ModelNames.Label("gpt-4.1"));
        Assert.Equal(["claude-opus-5-5", "claude-fable-5-1", "claude-sonnet-5-5", "claude-haiku-5-5"], ModelNames.ClaudeFeatured);
        Assert.Equal("超高", ModelNames.EffortLabel("xhigh"));
        Assert.Equal("默认", ModelNames.EffortLabel(""));
    }

    [Fact]
    public async Task OpenAiReasoningEffortIsSentOnlyWhenChosen()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new AiClient(http);
        await foreach (var _ in client.StreamAsync(new ProviderSettings { Model = "gpt-4.1" }, "key", "s", [new ChatMessage("user", "hi")])) { }
        await foreach (var _ in client.StreamAsync(new ProviderSettings { Model = "o-reasoner", OpenAiReasoningEffort = "high" }, "key", "s", [new ChatMessage("user", "hi")])) { }
        Assert.Null(handler.Bodies[0]["reasoning_effort"]);
        Assert.Equal("high", (string?)handler.Bodies[1]["reasoning_effort"]);
        // 旧配置的 Effort 默认为 high，但只用于 Claude，不会被当成 OpenAI 的 reasoning_effort 发送。
        Assert.Equal("high", new ProviderSettings().Effort);
        Assert.Throws<ArgumentException>(() => new ProviderSettings { OpenAiReasoningEffort = "max" }.RequestUri());
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<JsonNode> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync())!);
            const string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
        }
    }
}

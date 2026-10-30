using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class TokenUsageTests
{
    private const string ClaudeStream =
        "data: {\"type\":\"message_start\",\"message\":{\"model\":\"claude-sonnet-5-5\",\"usage\":{\"input_tokens\":100,\"cache_read_input_tokens\":900,\"cache_creation_input_tokens\":50,\"output_tokens\":1}}}\n\n" +
        "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\n\n" +
        "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":42}}\n\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    private const string OpenAiStream =
        "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" +
        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":7,\"prompt_tokens_details\":{\"cached_tokens\":600}}}\n\n" +
        "data: [DONE]\n\n";

    private static readonly ProviderSettings Claude = new() { Provider = ProviderKind.Claude, Endpoint = "https://api.anthropic.com/v1", Model = "claude-opus-5-5" };

    [Fact]
    public async Task AgentTurnsReportClaudeUsageIncludingCache()
    {
        using var http = new HttpClient(new Handler(ClaudeStream));
        var turn = await new AgentProtocol(http).CompleteAsync(Claude, "key", "system", new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }), CancellationToken.None);
        Assert.Equal(new TokenUsage(100, 42, 900, 50), turn.Usage);
        Assert.Equal(1050, turn.Usage!.ContextTokens);
        Assert.Equal("claude-sonnet-5-5", turn.ServedModel);
    }

    [Fact]
    public async Task OpenAiUsageChunkIsReadAndStreamOptionsOnlyGoToOpenAi()
    {
        var handler = new Handler(OpenAiStream);
        using var http = new HttpClient(handler);
        TokenUsage? reported = null;
        var client = new AiClient(http, onUsage: u => reported = u);
        await foreach (var _ in client.StreamAsync(new ProviderSettings(), "key", "system", [new ChatMessage("user", "hi")])) { }
        Assert.Equal(new TokenUsage(400, 7, 600, 0), reported);
        Assert.NotNull(JsonNode.Parse(handler.Body!)!["stream_options"]);

        var gateway = new Handler(OpenAiStream);
        using var gatewayHttp = new HttpClient(gateway);
        var turn = await new AgentProtocol(gatewayHttp).CompleteAsync(new ProviderSettings { Endpoint = "https://gateway.example.com/v1" }, "key", "system",
            new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }), CancellationToken.None);
        Assert.Null(JsonNode.Parse(gateway.Body!)!["stream_options"]);
        Assert.Equal(1000, turn.Usage!.ContextTokens);
    }

    private sealed class Handler(string sse) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
        }
    }
}

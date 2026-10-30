using System.Net;
using System.Text;
using System.Text.Json;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class AiClientTests
{
    [Fact]
    public async Task OpenAiSendsCorrectRequestAndCombinesDeltas()
    {
        var handler = new StubHandler("data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\" 世界\"}}]}\n\ndata: [DONE]\n\n");
        using var http = new HttpClient(handler);
        Assert.Equal("Hello 世界", await Collect(new AiClient(http).StreamAsync(new(), "test-key", "system", [new("user", "question")])));
        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.Uri!.ToString());
        Assert.Equal("Bearer test-key", handler.Authorization);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("system", json.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.True(json.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(16000, json.RootElement.GetProperty("max_completion_tokens").GetInt32());
    }

    [Fact]
    public async Task ClaudeUsesMessagesApiAndSeparateSystemPrompt()
    {
        var handler = new StubHandler("event: ping\ndata: {\"type\":\"ping\"}\n\nevent: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\n\ndata: {\"type\":\"message_stop\"}\n\n");
        using var http = new HttpClient(handler);
        var settings = new ProviderSettings { Provider = ProviderKind.Claude, Endpoint = "https://api.anthropic.com/v1/", Model = "test-model" };
        Assert.Equal("hi", await Collect(new AiClient(http).StreamAsync(settings, "test-key", "system", [new("user", "question")])));
        Assert.Equal("https://api.anthropic.com/v1/messages", handler.Uri!.ToString());
        Assert.Equal("test-key", handler.AnthropicKey);
        Assert.Equal("2023-06-01", handler.AnthropicVersion);
        Assert.Null(handler.Authorization);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("system", json.RootElement.GetProperty("system").GetString());
        Assert.Equal("user", json.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal(16000, json.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("adaptive", json.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("high", json.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.False(json.RootElement.TryGetProperty("fallbacks", out _));
    }

    [Fact]
    public async Task HttpErrorDoesNotEchoSensitiveResponseBody()
    {
        using var http = new HttpClient(new StubHandler("secret echoed by proxy", HttpStatusCode.Unauthorized));
        var error = await Assert.ThrowsAsync<ProviderHttpException>(() => Collect(new AiClient(http).StreamAsync(new(), "key", "system", [new("user", "x")])));
        Assert.Equal(HttpStatusCode.Unauthorized, error.Status);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Theory]
    [InlineData("data: {\"error\":{\"message\":\"secret\"}}\n\n")]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n")]
    [InlineData("data: {\"choices\":[{\"finish_reason\":\"length\",\"delta\":{}}]}\n\ndata: [DONE]\n\n")]
    public async Task IncompleteOrFailedStreamsAreRejected(string stream)
    {
        using var http = new HttpClient(new StubHandler(stream));
        await Assert.ThrowsAsync<IOException>(() => Collect(new AiClient(http).StreamAsync(new(), "key", "system", [new("user", "x")])));
    }

    [Fact]
    public async Task ClaudeTruncatedOutputIsRejected()
    {
        using var http = new HttpClient(new StubHandler("data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"max_tokens\"}}\n\ndata: {\"type\":\"message_stop\"}\n\n"));
        await Assert.ThrowsAsync<IOException>(() => Collect(new AiClient(http).StreamAsync(new() { Provider = ProviderKind.Claude }, "key", "system", [new("user", "x")])));
    }

    [Fact]
    public async Task ClaudeRefusalIsRejected()
    {
        using var http = new HttpClient(new StubHandler("data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"refusal\"}}\n\ndata: {\"type\":\"message_stop\"}\n\n"));
        await Assert.ThrowsAsync<IOException>(() => Collect(new AiClient(http).StreamAsync(new() { Provider = ProviderKind.Claude }, "key", "system", [new("user", "x")])));
    }

    [Fact]
    public async Task CancellationIsPropagated()
    {
        using var http = new HttpClient(new StubHandler("data: [DONE]\n\n"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(new AiClient(http).StreamAsync(new(), "key", "system", [new("user", "x")], cts.Token)));
    }

    [Fact]
    public async Task MissingKeyDoesNotSendHttpRequest()
    {
        var handler = new StubHandler("");
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => Collect(new AiClient(http).StreamAsync(new(), "", "system", [new("user", "x")])));
        Assert.Null(handler.Uri);
    }

    private static async Task<string> Collect(IAsyncEnumerable<string> stream)
    {
        var output = new StringBuilder();
        await foreach (var text in stream) output.Append(text);
        return output.ToString();
    }

    private sealed class StubHandler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }
        public string? Authorization { get; private set; }
        public string? AnthropicKey { get; private set; }
        public string? AnthropicVersion { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();
            if (request.Headers.TryGetValues("x-api-key", out var keys)) AnthropicKey = keys.Single();
            if (request.Headers.TryGetValues("anthropic-version", out var versions)) AnthropicVersion = versions.Single();
            return new(status) { Content = new StringContent(response, Encoding.UTF8, "text/event-stream") };
        }
    }
}

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

/// <summary>替换静态的 <see cref="ProviderRetry.Delay"/>：不与其他测试类并行，以免记录到其他测试的重试等待。</summary>
[CollectionDefinition(nameof(RetryTests), DisableParallelization = true)]
public sealed class RetryTestsCollection;

[Collection(nameof(RetryTests))]
public sealed class RetryTests : IDisposable
{
    private readonly List<TimeSpan> waits = [];
    private readonly Func<TimeSpan, CancellationToken, Task> originalDelay = ProviderRetry.Delay;

    public RetryTests() => ProviderRetry.Delay = (delay, token) =>
    {
        token.ThrowIfCancellationRequested();
        lock (waits) waits.Add(delay);
        return Task.CompletedTask;
    };

    public void Dispose() => ProviderRetry.Delay = originalDelay;

    private static string Finish() => Sse.Convert("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"Done\"}}]}");
    private static JsonArray Messages() => new(new JsonObject { ["role"] = "user", ["content"] = "hi" });

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(529)]
    public async Task TransientStatusIsRetriedThenSucceeds(int status)
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage((HttpStatusCode)status), _ => Ok(Finish()));
        var retries = new List<int>();
        using var http = new HttpClient(handler);
        var turn = await new AgentProtocol(http, (attempt, _) => retries.Add(attempt)).CompleteAsync(new(), "key", "system", Messages(), CancellationToken.None);
        Assert.Equal("Done", turn.Text);
        Assert.Equal(2, handler.Count);
        Assert.Equal([1], retries);
        Assert.Equal([TimeSpan.FromSeconds(1)], waits);
    }

    [Fact]
    public async Task RetryAfterHeaderIsHonoredAndCapped()
    {
        var handler = new ScriptedHandler(
            _ => WithRetryAfter((HttpStatusCode)429, TimeSpan.FromSeconds(3)),
            _ => WithRetryAfter((HttpStatusCode)429, TimeSpan.FromMinutes(5)),
            _ => Ok(Finish()));
        using var http = new HttpClient(handler);
        await new AgentProtocol(http).CompleteAsync(new(), "key", "system", Messages(), CancellationToken.None);
        Assert.Equal([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(20)], waits);
    }

    [Fact]
    public async Task NonTransientStatusIsNotRetried()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ProviderHttpException>(() => new AgentProtocol(http).CompleteAsync(new(), "key", "system", Messages(), CancellationToken.None));
        Assert.Equal(HttpStatusCode.BadRequest, error.Status);
        Assert.Equal(1, handler.Count);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task GivesUpAfterMaxAttempts()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ProviderHttpException>(() => new AgentProtocol(http).CompleteAsync(new(), "key", "system", Messages(), CancellationToken.None));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.Status);
        Assert.Equal(ProviderRetry.MaxAttempts, handler.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], waits);
    }

    [Fact]
    public async Task ConnectionFailureIsRetriedForStreamingChat()
    {
        var handler = new ScriptedHandler(_ => throw new HttpRequestException("connection reset"),
            _ => Ok("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n"));
        using var http = new HttpClient(handler);
        var text = new StringBuilder();
        await foreach (var chunk in new AiClient(http).StreamAsync(new(), "key", "system", [new("user", "x")])) text.Append(chunk);
        Assert.Equal("ok", text.ToString());
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task CancellationDuringBackoffStopsRetrying()
    {
        using var cts = new CancellationTokenSource();
        var handler = new ScriptedHandler(_ => { cts.Cancel(); return new HttpResponseMessage((HttpStatusCode)429); });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AgentProtocol(http).CompleteAsync(new(), "key", "system", Messages(), cts.Token));
        Assert.Equal(1, handler.Count);
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage WithRetryAfter(HttpStatusCode status, TimeSpan delay)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delay);
        return response;
    }

    private sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps) : HttpMessageHandler
    {
        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = steps[Math.Min(Count, steps.Length - 1)];
            Count++;
            return Task.FromResult(step(request));
        }
    }
}

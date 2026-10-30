using System.Text.Json.Nodes;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class ContextCompactorTests
{
    private static JsonObject User(string text) => new() { ["role"] = "user", ["content"] = text };

    private static AgentConversation TwoRuns() => new(new JsonArray
    {
        User("[Writes are enabled]\n\nRename A to B"),
        new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(
            new JsonObject { ["type"] = "thinking", ["thinking"] = "secret plan", ["signature"] = "sig" },
            new JsonObject { ["type"] = "tool_use", ["id"] = "t1", ["name"] = "read_file", ["input"] = new JsonObject { ["path"] = "A.cs" } }) },
        new JsonObject { ["role"] = "user", ["content"] = new JsonArray(
            new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "t1", ["content"] = new string('x', 5000) }) },
        new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Renamed." }) },
        User("[Writes are enabled]\n\nNow add tests"),
        new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Added." }) },
    }, interrupted: false);

    [Fact]
    public void ThresholdIsEightyPercentAndZeroDisables()
    {
        Assert.False(ContextCompactor.ShouldCompact(160_000, 200_000));
        Assert.True(ContextCompactor.ShouldCompact(160_001, 200_000));
        Assert.False(ContextCompactor.ShouldCompact(10_000_000, 0));
    }

    [Fact]
    public async Task ClientCompactionReplacesTheWholeAgentHistoryAndKeepsNoThinking()
    {
        var conversation = TwoRuns();
        string? transcript = null;
        var done = await ContextCompactor.CompactAgentAsync((t, _) => { transcript = t; return Task.FromResult("User renamed A to B, then asked for tests."); },
            conversation, CancellationToken.None);
        Assert.True(done);
        var messages = conversation.Export();
        Assert.Equal(2, messages.Count);
        Assert.StartsWith(ContextCompactor.SummaryPrefix, (string?)messages[0]!["content"]);
        Assert.Equal("assistant", (string?)messages[1]!["role"]);
        Assert.DoesNotContain("thinking", messages.ToJsonString());
        Assert.Contains("Tool call: read_file", transcript);
        Assert.Contains("Now add tests", transcript);
        Assert.Contains("[truncated]", transcript);
        Assert.DoesNotContain("secret plan", transcript);
    }

    [Fact]
    public async Task ServerCompactionKeepsOnlyTheReturnedBlockUnchanged()
    {
        var conversation = TwoRuns();
        var block = new JsonObject { ["type"] = "compaction", ["content"] = "Summary.", ["signature"] = "EuYB" };
        JsonArray? sent = null;
        Assert.True(await ContextCompactor.CompactAgentOnServerAsync((history, _) => { sent = history; return Task.FromResult(block); }, conversation, CancellationToken.None));
        Assert.Equal(6, sent!.Count);
        Assert.Contains("secret plan", sent.ToJsonString());
        var messages = conversation.Export();
        Assert.Equal("[{\"role\":\"assistant\",\"content\":[{\"type\":\"compaction\",\"content\":\"Summary.\",\"signature\":\"EuYB\"}]}]", messages.ToJsonString());
    }

    [Fact]
    public async Task NothingToCompactSkipsTheModelAndFailuresLeaveHistoryUnchanged()
    {
        var single = new AgentConversation(new JsonArray(User("only message")), interrupted: false);
        Assert.False(await ContextCompactor.CompactAgentAsync((_, _) => throw new InvalidOperationException("should not be called"), single, CancellationToken.None));

        var conversation = TwoRuns();
        var before = conversation.Export().ToJsonString();
        await Assert.ThrowsAsync<IOException>(() => ContextCompactor.CompactAgentAsync((_, _) => throw new IOException("down"), conversation, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => ContextCompactor.CompactAgentOnServerAsync((_, _) => throw new IOException("down"), conversation, CancellationToken.None));
        Assert.Equal(before, conversation.Export().ToJsonString());
    }

    [Fact]
    public async Task ChatHistoryKeepsRecentTurns()
    {
        var history = Enumerable.Range(1, 6).SelectMany(i => new[] { new ChatMessage("user", "q" + i), new ChatMessage("assistant", "a" + i) }).ToList();
        var compacted = await ContextCompactor.CompactChatAsync((t, _) => Task.FromResult("summary of q1-q4"), history, 2, CancellationToken.None);
        Assert.Equal(["user", "assistant", "user", "assistant", "user", "assistant"], compacted!.Select(m => m.Role));
        Assert.Equal("q5", compacted![2].Content);
        Assert.Null(await ContextCompactor.CompactChatAsync((_, _) => Task.FromResult("x"), history.Take(4).ToList(), 2, CancellationToken.None));
    }
}

public sealed class AgentCompactionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SLCopilot-compact-" + Guid.NewGuid().ToString("N"));
    public AgentCompactionTests() => Directory.CreateDirectory(Path.Combine(root, "project"));
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private static AgentConversation History() => new(new JsonArray
    {
        new JsonObject { ["role"] = "user", ["content"] = "first task" },
        new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "done 1" }) },
        new JsonObject { ["role"] = "user", ["content"] = "second task" },
        new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "done 2" }) },
    }, interrupted: false) { LastContextTokens = 900 };

    private async Task<(Handler Handler, List<AgentEvent> Events)> RunAsync(string endpoint)
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var settings = new ProviderSettings { Provider = ProviderKind.Claude, Endpoint = endpoint, Model = "claude-opus-5-5", ContextWindow = 1000 };
        var events = new List<AgentEvent>();
        await new AgentRunner(new AgentProtocol(http)).RunAsync(settings, "key", "third task",
            new WorkspaceTools(Path.Combine(root, "project"), false, Path.Combine(root, "backups")), events.Add, CancellationToken.None, conversation: History());
        return (handler, events);
    }

    [Fact]
    public async Task OfficialEndpointCompactsOnTheServerAndSendsTheBlockFirst()
    {
        var (handler, events) = await RunAsync("https://api.anthropic.com/v1");
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal("summarize", (string?)handler.Bodies[0]["compaction"]!["type"]);
        Assert.Contains("Do not call tools", (string?)handler.Bodies[0]["compaction"]!["instructions"]);
        Assert.Null(handler.Bodies[0]["fallbacks"]);
        Assert.Contains("compact-2026-09-04", handler.Betas[0]);
        var sent = handler.Bodies[1]["messages"]!.AsArray();
        Assert.Equal(2, sent.Count);
        Assert.Equal("compaction", (string?)sent[0]!["content"]![0]!["type"]);
        Assert.Equal("EuYB", (string?)sent[0]!["content"]![0]!["signature"]);
        Assert.EndsWith("third task", (string?)sent[1]!["content"]);
        Assert.Contains("compact-2026-09-04", handler.Betas[1]);
        Assert.Contains(events, e => e.Kind == "compacted" && e.Message == "900");
    }

    [Fact]
    public async Task GatewaysCompactOnTheClientWithASummaryOnly()
    {
        var (handler, _) = await RunAsync("https://gateway.example.com/v1");
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Null(handler.Bodies[0]["tools"]);
        Assert.Null(handler.Bodies[0]["compaction"]);
        var sent = handler.Bodies[1]["messages"]!.AsArray();
        Assert.Equal(3, sent.Count);
        Assert.StartsWith(ContextCompactor.SummaryPrefix, (string?)sent[0]!["content"]);
        Assert.EndsWith("third task", (string?)sent[2]!["content"]);
        Assert.DoesNotContain("compact-2026-09-04", handler.Betas[1]);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<JsonNode> Bodies { get; } = [];
        public List<string> Betas { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Bodies.Add(body);
            Betas.Add(request.Headers.TryGetValues("anthropic-beta", out var values) ? string.Join(",", values) : "");
            if (body["compaction"] is not null)
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"compaction\",\"content\":\"Summary: two tasks done.\",\"signature\":\"EuYB\"}],\"stop_reason\":\"compaction\"}",
                        System.Text.Encoding.UTF8, "application/json"),
                };
            var text = body["tools"] is null ? "Summary: first and second task done." : "All done.";
            var sse = Sse.Convert("{\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}],\"stop_reason\":\"end_turn\"}");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream") };
        }
    }
}

public sealed class ThinkingRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SLCopilot-strip-" + Guid.NewGuid().ToString("N"));
    public ThinkingRecoveryTests() => Directory.CreateDirectory(Path.Combine(root, "project"));
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    [Fact]
    public void StrippingKeepsTextAndToolCallsAndAlternation()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "q" },
            new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(
                new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "s" },
                new JsonObject { ["type"] = "tool_use", ["id"] = "t1", ["name"] = "read_file", ["input"] = new JsonObject() }) },
            new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "redacted_thinking", ["data"] = "x" }) },
        };
        Assert.True(AgentProtocol.ContainsThinking(messages));
        var stripped = AgentProtocol.StripThinking(messages);
        Assert.False(AgentProtocol.ContainsThinking(stripped));
        Assert.Equal("tool_use", (string?)stripped[1]!["content"]![0]!["type"]);
        Assert.Equal("text", (string?)stripped[2]!["content"]![0]!["type"]);
        Assert.True(AgentProtocol.ContainsThinking(messages));
    }

    [Fact]
    public async Task A400WithThinkingInHistoryIsRetriedOnceWithoutThinking()
    {
        var bodies = new List<JsonNode>();
        using var http = new HttpClient(new Handler(bodies));
        var conversation = new AgentConversation(new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "old task" },
            new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(
                new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "old" },
                new JsonObject { ["type"] = "text", ["text"] = "old answer" }) },
        }, interrupted: false);
        var events = new List<AgentEvent>();
        var settings = new ProviderSettings { Provider = ProviderKind.Claude, Endpoint = "https://gateway.example.com/v1", Model = "claude-opus-5-5" };
        await new AgentRunner(new AgentProtocol(http)).RunAsync(settings, "key", "new task",
            new WorkspaceTools(Path.Combine(root, "project"), false, Path.Combine(root, "backups")), events.Add, CancellationToken.None, conversation: conversation);
        Assert.Equal(2, bodies.Count);
        Assert.Contains("\"signature\":\"old\"", bodies[0].ToJsonString());
        Assert.DoesNotContain("\"thinking\"", bodies[1]["messages"]!.ToJsonString());
        Assert.Contains(events, e => e.Kind == "thinking_stripped");
        Assert.False(AgentProtocol.ContainsThinking(conversation.Export()));
    }

    private sealed class Handler(List<JsonNode> bodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            bodies.Add(body);
            if (body["messages"]!.ToJsonString().Contains("\"signature\":\"old\""))
                return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest) { Content = new StringContent("{}") };
            var sse = Sse.Convert("{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"stop_reason\":\"end_turn\"}");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream") };
        }
    }
}

public sealed class ReviewFixTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SLCopilot-review-" + Guid.NewGuid().ToString("N"));
    public ReviewFixTests() => Directory.CreateDirectory(Path.Combine(root, "project"));
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private WorkspaceTools Workspace() => new(Path.Combine(root, "project"), false, Path.Combine(root, "backups"));

    [Fact]
    public void InstructionsAreConsideredSentOnlyWhileTheyAreInTheHistory()
    {
        var block = new ProjectInstructions(".slcopilot/instructions.md", "用中文注释").ToPromptBlock();
        var conversation = new AgentConversation(new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "[Writes are enabled]\n\n" + block + "\n\ntask" }), false);
        Assert.True(conversation.HasUserMessageContaining(block));
        Assert.False(new AgentConversation().HasUserMessageContaining(block));
        var compacted = new AgentConversation(new JsonArray(new JsonObject { ["role"] = "user", ["content"] = ContextCompactor.SummaryPrefix + "\nsummary" }), false);
        Assert.False(compacted.HasUserMessageContaining(block));
    }

    [Fact]
    public async Task OfficialEndpoint400IsNotRetriedWithoutThinking()
    {
        var count = 0;
        using var http = new HttpClient(new Handler(_ => { count++; return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest) { Content = new StringContent("{}") }; }));
        var conversation = new AgentConversation(new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "old" },
            new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "s" }, new JsonObject { ["type"] = "text", ["text"] = "a" }) },
        }, false);
        var settings = new ProviderSettings { Provider = ProviderKind.Claude, Endpoint = "https://api.anthropic.com/v1", Model = "claude-opus-5-5" };
        await Assert.ThrowsAsync<ProviderHttpException>(() => new AgentRunner(new AgentProtocol(http)).RunAsync(settings, "key", "new", Workspace(), _ => { }, CancellationToken.None, conversation: conversation));
        Assert.Equal(1, count);
        Assert.True(AgentProtocol.ContainsThinking(conversation.Export()));
    }

    [Fact]
    public async Task FailedCompactionIsNotRetriedOnEveryToolTurn()
    {
        var compactionRequests = 0;
        var agentTurns = 0;
        using var http = new HttpClient(new Handler(body =>
        {
            if (body["tools"] is null) { compactionRequests++; return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable); }
            agentTurns++;
            var json = agentTurns < 4
                ? "{\"content\":[{\"type\":\"tool_use\",\"id\":\"t" + agentTurns + "\",\"name\":\"get_errors\",\"input\":{}}],\"stop_reason\":\"tool_use\"}"
                : "{\"content\":[{\"type\":\"text\",\"text\":\"done\"}],\"stop_reason\":\"end_turn\"}";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Sse.Convert(json), System.Text.Encoding.UTF8, "text/event-stream") };
        }));
        var conversation = new AgentConversation(new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "earlier" },
            new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "ok" }) },
        }, false);
        var settings = new ProviderSettings { Provider = ProviderKind.Claude, Endpoint = "https://gateway.example.com/v1", Model = "claude-opus-5-5", ContextWindow = 1000 };
        var events = new List<AgentEvent>();
        await new AgentRunner(new AgentProtocol(http)).RunAsync(settings, "key", new string('x', 5000), Workspace(), events.Add, CancellationToken.None, conversation: conversation);
        Assert.Equal(4, agentTurns);
        Assert.Equal(ProviderRetry.MaxAttempts, compactionRequests);
        Assert.Single(events, e => e.Kind == "compact_failed");
    }

    private sealed class Handler(Func<JsonNode, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(JsonNode.Parse(await request.Content!.ReadAsStringAsync())!);
    }
}

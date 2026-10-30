using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class AgentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeToolRoundTripWritesARealFileAndReportsSuccess(bool claude)
    {
        var temp = Path.Combine(Path.GetTempPath(), "SLCopilot-loop-tests-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(temp, "project");
        Directory.CreateDirectory(root);
        try
        {
            var handler = new SequenceHandler((turn, request) =>
            {
                Assert.True(request["stream"]!.GetValue<bool>());
                Assert.Equal(27, request["tools"]!.AsArray().Count);
                var messages = request["messages"]!.AsArray();
                if (turn == 0)
                {
                    var args = new { path = "Hello.cs", content = "class Hello {}\n", expected_sha256 = "new" };
                    return claude
                        ? JsonSerializer.Serialize(new { stop_reason = "tool_use", content = new object[] { new { type = "tool_use", id = "call_1", name = "write_file", input = args } } })
                        : JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "tool_calls", message = new { role = "assistant", content = (string?)null,
                            tool_calls = new[] { new { id = "call_1", type = "function", function = new { name = "write_file", arguments = JsonSerializer.Serialize(args) } } } } } } });
                }
                Assert.Equal(1, turn);
                if (claude)
                {
                    Assert.Equal("assistant", messages[^2]!["role"]!.GetValue<string>());
                    Assert.Equal("tool_use", messages[^2]!["content"]![0]!["type"]!.GetValue<string>());
                    Assert.Equal("call_1", messages[^1]!["content"]![0]!["tool_use_id"]!.GetValue<string>());
                    Assert.False(messages[^1]!["content"]![0]!["is_error"]!.GetValue<bool>());
                }
                else
                {
                    Assert.Equal("call_1", messages[^2]!["tool_calls"]![0]!["id"]!.GetValue<string>());
                    Assert.Equal("tool", messages[^1]!["role"]!.GetValue<string>());
                    Assert.Equal("call_1", messages[^1]!["tool_call_id"]!.GetValue<string>());
                }
                return Finish(claude);
            });
            using var http = new HttpClient(handler);
            var workspace = new WorkspaceTools(root, true, Path.Combine(temp, "backups"));
            var events = new List<AgentEvent>();
            await new AgentRunner(new AgentProtocol(http)).RunAsync(new() { Provider = claude ? ProviderKind.Claude : ProviderKind.OpenAI },
                "test-key", "Create Hello.cs", workspace, events.Add, CancellationToken.None);
            Assert.Equal("class Hello {}\n", await File.ReadAllTextAsync(Path.Combine(root, "Hello.cs")));
            Assert.Contains(events, e => e.Kind == "tool");
            Assert.Contains(events, e => e.Kind == "changes");
            Assert.Contains(events, e => e.Kind == "complete");
            Assert.Equal(2, handler.Count);
        }
        finally { Directory.Delete(temp, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SteeringTextReachesTheModelWithTheToolResultsAndWritesReportADiff(bool claude)
    {
        var temp = Path.Combine(Path.GetTempPath(), "SLCopilot-steer-tests-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(temp, "project");
        Directory.CreateDirectory(root);
        try
        {
            var handler = new SequenceHandler((turn, request) =>
            {
                var messages = request["messages"]!.AsArray();
                if (turn == 0)
                {
                    var args = new { path = "Hello.cs", content = "class Hello {}\n", expected_sha256 = "new" };
                    return claude
                        ? JsonSerializer.Serialize(new { stop_reason = "tool_use", content = new object[] { new { type = "tool_use", id = "call_1", name = "write_file", input = args } } })
                        : JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "tool_calls", message = new { role = "assistant", content = (string?)null,
                            tool_calls = new[] { new { id = "call_1", type = "function", function = new { name = "write_file", arguments = JsonSerializer.Serialize(args) } } } } } } });
                }
                var last = messages[^1]!;
                if (claude)
                {
                    Assert.Equal("tool_result", last["content"]![0]!["type"]!.GetValue<string>());
                    Assert.Contains("also add a comment", last["content"]![1]!["text"]!.GetValue<string>());
                }
                else
                {
                    Assert.Equal("tool", messages[^2]!["role"]!.GetValue<string>());
                    Assert.Equal("user", last["role"]!.GetValue<string>());
                    Assert.Contains("also add a comment", last["content"]!.GetValue<string>());
                }
                return Finish(claude);
            });
            using var http = new HttpClient(handler);
            var workspace = new WorkspaceTools(root, true, Path.Combine(temp, "backups"));
            var events = new List<AgentEvent>();
            var queued = "also add a comment";
            await new AgentRunner(new AgentProtocol(http)).RunAsync(new() { Provider = claude ? ProviderKind.Claude : ProviderKind.OpenAI },
                "test-key", "Create Hello.cs", workspace, events.Add, CancellationToken.None, steer: () => Interlocked.Exchange(ref queued, null));
            Assert.Contains(events, e => e.Kind == "steered" && e.Message == "also add a comment");
            Assert.Contains(events, e => e.Kind == "tool_result" && e.Message.StartsWith("+1 −0" + AgentRunner.ToolDetailSeparator + "path: Hello.cs", StringComparison.Ordinal));
            Assert.Contains(events, e => e.Kind == "diff" && e.Message.StartsWith("Hello.cs\n@@ -1 +1 @@\n+class Hello {}", StringComparison.Ordinal));
        }
        finally { Directory.Delete(temp, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TruncatedToolResponsesNeverExecute(bool claude)
    {
        using var http = new HttpClient(new SequenceHandler((_, _) => claude
            ? "{\"stop_reason\":\"max_tokens\",\"content\":[]}"
            : "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{}}]}"));
        await Assert.ThrowsAsync<IOException>(() => new AgentProtocol(http).CompleteAsync(
            new() { Provider = claude ? ProviderKind.Claude : ProviderKind.OpenAI }, "key", "system",
            new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }), CancellationToken.None));
    }

    [Fact]
    public async Task MalformedArgumentsAreRejectedBeforeToolsRun()
    {
        using var http = new HttpClient(new SequenceHandler((_, _) =>
            "{\"choices\":[{\"finish_reason\":\"tool_calls\",\"message\":{\"tool_calls\":[{\"id\":\"x\",\"type\":\"function\",\"function\":{\"name\":\"write_file\",\"arguments\":\"[]\"}}]}}]}"));
        await Assert.ThrowsAsync<IOException>(() => new AgentProtocol(http).CompleteAsync(new(), "key", "system", new(), CancellationToken.None));
    }

    [Fact]
    public void MultipleClaudeToolResultsStayInOneUserMessage()
    {
        var messages = new JsonArray();
        using var doc = JsonDocument.Parse("{}");
        AgentProtocol.AppendResults(messages, true,
            [(new("one", "read_file", doc.RootElement), new("read", false)),
             (new("two", "write_file", doc.RootElement), new("denied", true))]);
        Assert.Single(messages);
        Assert.Equal(2, messages[0]!["content"]!.AsArray().Count);
        Assert.True(messages[0]!["content"]![1]!["is_error"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentReadsThenEditsExistingCodeAndRemembersCompletedTask(bool claude)
    {
        var temp = Path.Combine(Path.GetTempPath(), "SLCopilot-edit-tests-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(temp, "project");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "A.cs"), "class Before {}\n");
            var handler = new SequenceHandler((turn, request) =>
            {
                if (turn == 0) return Tool(claude, "read_file", new { path = "A.cs" });
                if (turn == 1)
                {
                    var last = request["messages"]!.AsArray()[^1]!;
                    var result = claude ? last["content"]![0]!["content"]!.GetValue<string>() : last["content"]!.GetValue<string>();
                    var read = JsonNode.Parse(result)!;
                    Assert.Contains("Before", read["content"]!.GetValue<string>());
                    return Tool(claude, "edit_file", new { path = "A.cs", old_text = "Before", new_text = "After", expected_sha256 = read["sha256"]!.GetValue<string>() });
                }
                if (turn == 3)
                {
                    var messages = request["messages"]!.AsArray();
                    Assert.Contains("Rename class", messages.ToJsonString());
                    Assert.Contains("Done", messages.ToJsonString());
                    Assert.EndsWith("Explain your change", messages[^1]!["content"]!.GetValue<string>());
                }
                return Finish(claude);
            });
            using var http = new HttpClient(handler);
            var runner = new AgentRunner(new AgentProtocol(http));
            var settings = new ProviderSettings { Provider = claude ? ProviderKind.Claude : ProviderKind.OpenAI };
            var conversation = new AgentConversation();
            await runner.RunAsync(settings, "key", "Rename class", new WorkspaceTools(root, true, Path.Combine(temp, "backups")),
                _ => { }, CancellationToken.None, conversation: conversation);
            Assert.Equal("class After {}\n", await File.ReadAllTextAsync(Path.Combine(root, "A.cs")));
            await runner.RunAsync(settings, "key", "Explain your change", new WorkspaceTools(root, false, Path.Combine(temp, "backups")),
                _ => { }, CancellationToken.None, conversation: conversation);
            Assert.Equal(4, handler.Count);
            Assert.False(conversation.Interrupted);
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public async Task PlanRunsAreReadOnlyAndAskForAPlan()
    {
        var temp = Path.Combine(Path.GetTempPath(), "SLCopilot-plan-tests-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(temp, "project");
        Directory.CreateDirectory(root);
        try
        {
            var handler = new SequenceHandler((_, _) => Finish(false));
            using var http = new HttpClient(handler);
            var runner = new AgentRunner(new AgentProtocol(http));
            await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(new(), "key", "Plan", new WorkspaceTools(root, true, Path.Combine(temp, "backups")),
                _ => { }, CancellationToken.None, plan: true));
            Assert.Equal(0, handler.Count);
            var conversation = new AgentConversation();
            await runner.RunAsync(new(), "key", "Plan it", new WorkspaceTools(root, false, Path.Combine(temp, "backups")), _ => { }, CancellationToken.None,
                conversation: conversation, plan: true);
            var first = handler.Requests[0]["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>();
            Assert.StartsWith(AgentRunner.PlanModeNote, first);
            await runner.RunAsync(new(), "key", AgentRunner.ExecutePlanPrompt, new WorkspaceTools(root, true, Path.Combine(temp, "backups")), _ => { },
                CancellationToken.None, conversation: conversation);
            var messages = handler.Requests[1]["messages"]!.AsArray();
            Assert.Contains(messages, m => (string?)m!["content"] is { } text && text.Contains("Plan it"));
            Assert.StartsWith("[Writes are enabled for this run.]", messages[^1]!["content"]!.GetValue<string>());
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public async Task TurnLimitStopsLoopAndInterruptedHistoryResumesWithPairedResults()
    {
        var temp = Path.Combine(Path.GetTempPath(), "SLCopilot-limit-tests-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(temp, "project");
        Directory.CreateDirectory(root);
        try
        {
            var handler = new SequenceHandler((turn, request) =>
            {
                if (turn < 2) return Tool(false, "list_directory", new { path = "." });
                var messages = request["messages"]!.AsArray();
                Assert.Contains("previous run was interrupted", messages[^1]!["content"]!.GetValue<string>());
                Assert.Equal("tool", messages[^2]!["role"]!.GetValue<string>());
                return Finish(false);
            });
            using var http = new HttpClient(handler);
            var runner = new AgentRunner(new AgentProtocol(http));
            var workspace = new WorkspaceTools(root, false, Path.Combine(temp, "backups"));
            var conversation = new AgentConversation();
            await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(new(), "key", "Loop", workspace, _ => { }, CancellationToken.None,
                maxTurns: 2, conversation: conversation));
            Assert.True(conversation.Interrupted);
            Assert.Equal(2, handler.Count);
            await runner.RunAsync(new(), "key", "Continue", workspace, _ => { }, CancellationToken.None, conversation: conversation);
            Assert.False(conversation.Interrupted);
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public async Task ClaudeFirstPartyRequestUsesOpus55Options()
    {
        var handler = new SequenceHandler((_, _) => Finish(true));
        using var http = new HttpClient(handler);
        await new AgentProtocol(http).CompleteAsync(Opus, "key", "system",
            new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }), CancellationToken.None);
        var request = handler.Requests[0];
        Assert.Equal("adaptive", request["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("high", request["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal("default", request["fallbacks"]!.GetValue<string>());
        Assert.Equal("ephemeral", request["cache_control"]!["type"]!.GetValue<string>());
        Assert.Equal(16000, request["max_tokens"]!.GetValue<int>());
        Assert.Equal("server-side-fallback-2026-07-01,thinking-display-updates-2026-08-18,thinking-binding-controls-2026-08-01", handler.Betas[0]);
        Assert.Null(request["context_management"]);
        Assert.Equal("updates", request["thinking"]!["display"]!.GetValue<string>());
        Assert.Equal("drop_block", request["thinking"]!["block_binding"]!["prefix_mismatch_behavior"]!.GetValue<string>());
    }

    [Fact]
    public async Task ThirdPartyClaudeGatewayGetsNoBetaFields()
    {
        var handler = new SequenceHandler((_, _) => Finish(true));
        using var http = new HttpClient(handler);
        await new AgentProtocol(http).CompleteAsync(Opus with { Endpoint = "https://gateway.example.com/v1" }, "key", "system",
            new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }), CancellationToken.None);
        var request = handler.Requests[0].AsObject();
        Assert.False(request.ContainsKey("fallbacks"));
        Assert.False(request.ContainsKey("cache_control"));
        Assert.True(request.ContainsKey("thinking"));
        Assert.False(request["thinking"]!.AsObject().ContainsKey("display"));
        Assert.False(request["thinking"]!.AsObject().ContainsKey("block_binding"));
        Assert.False(request.ContainsKey("context_management"));
        Assert.Null(handler.Betas[0]);
    }

    [Fact]
    public async Task FallbackBoundaryDropsEarlierNonTextBlocks()
    {
        using var http = new HttpClient(new SequenceHandler((_, _) => JsonSerializer.Serialize(new
        {
            stop_reason = "end_turn",
            content = new object[]
            {
                new { type = "thinking", thinking = "", signature = "old" },
                new { type = "text", text = "partial " },
                new { type = "fallback", from = new { model = "a" }, to = new { model = "b" } },
                new { type = "thinking", thinking = "", signature = "new" },
                new { type = "text", text = "done" },
            },
        })));
        var turn = await new AgentProtocol(http).CompleteAsync(Opus, "key", "system",
            new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }), CancellationToken.None);
        var blocks = turn.Message["content"]!.AsArray();
        Assert.Equal(["text", "thinking", "text"], blocks.Select(b => b!["type"]!.GetValue<string>()));
        Assert.Equal("new", blocks[1]!["signature"]!.GetValue<string>());
        Assert.Equal("partial done", turn.Text);
    }

    [Fact]
    public async Task SystemPromptAndToolsStayStableAndThinkingIsReplayedVerbatim()
    {
        var temp = Path.Combine(Path.GetTempPath(), "SLCopilot-prefix-tests-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(temp, "project");
        Directory.CreateDirectory(root);
        try
        {
            var handler = new SequenceHandler((turn, _) => turn == 0
                ? JsonSerializer.Serialize(new { stop_reason = "tool_use", content = new object[]
                    { new { type = "thinking", thinking = "", signature = "sig-1" }, new { type = "tool_use", id = "call_1", name = "list_directory", input = new { path = "." } } } })
                : Finish(true));
            using var http = new HttpClient(handler);
            var runner = new AgentRunner(new AgentProtocol(http));
            var conversation = new AgentConversation();
            await runner.RunAsync(Opus, "key", "Look", new WorkspaceTools(root, false, Path.Combine(temp, "backups")), _ => { }, CancellationToken.None, conversation: conversation);
            await runner.RunAsync(Opus, "key", "Now change it", new WorkspaceTools(root, true, Path.Combine(temp, "backups")), _ => { }, CancellationToken.None, conversation: conversation);

            Assert.Equal(3, handler.Count);
            for (var i = 1; i < handler.Count; i++)
            {
                Assert.Equal(handler.Requests[0]["system"]!.ToJsonString(), handler.Requests[i]["system"]!.ToJsonString());
                Assert.Equal(handler.Requests[0]["tools"]!.ToJsonString(), handler.Requests[i]["tools"]!.ToJsonString());
                var previous = handler.Requests[i - 1]["messages"]!.AsArray();
                var current = handler.Requests[i]["messages"]!.AsArray();
                for (var m = 0; m < previous.Count; m++) Assert.Equal(previous[m]!.ToJsonString(), current[m]!.ToJsonString());
            }
            var replayed = handler.Requests[1]["messages"]![1]!["content"]![0]!;
            Assert.Equal("sig-1", replayed["signature"]!.GetValue<string>());
            Assert.Contains("read-only", handler.Requests[0]["messages"]![0]!["content"]!.GetValue<string>());
            Assert.Contains("Writes are enabled", handler.Requests[2]["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>());
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public async Task RejectedWriteIsNotPerformedAndModelIsTold()
    {
        var temp = Path.Combine(Path.GetTempPath(), "SLCopilot-approval-tests-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(temp, "project");
        Directory.CreateDirectory(root);
        try
        {
            var proposals = new List<WriteProposal>();
            var workspace = new WorkspaceTools(root, true, Path.Combine(temp, "backups"),
                confirmWrite: (proposal, _) => { proposals.Add(proposal); return Task.FromResult(false); });
            var result = await workspace.ExecuteAsync("write_file",
                JsonSerializer.SerializeToElement(new { path = "src/New.cs", content = "class New {}\n", expected_sha256 = "new" }), CancellationToken.None);
            Assert.True(result.IsError);
            Assert.Contains("rejected", result.Content);
            Assert.False(File.Exists(Path.Combine(root, "src", "New.cs")));
            Assert.False(Directory.Exists(Path.Combine(temp, "backups")));
            var proposal = Assert.Single(proposals);
            Assert.True(proposal.IsNewFile);
            Assert.Equal("src/New.cs", proposal.RelativePath);
            Assert.Contains("+class New {}", proposal.Diff);
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public async Task CancellationWhileAwaitingApprovalPropagates()
    {
        var temp = Path.Combine(Path.GetTempPath(), "SLCopilot-approval-tests-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(temp, "project");
        Directory.CreateDirectory(root);
        try
        {
            var workspace = new WorkspaceTools(root, true, Path.Combine(temp, "backups"),
                confirmWrite: (_, _) => Task.FromCanceled<bool>(new CancellationToken(true)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.ExecuteAsync("write_file",
                JsonSerializer.SerializeToElement(new { path = "New.cs", content = "x", expected_sha256 = "new" }), CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(root, "New.cs")));
        }
        finally { Directory.Delete(temp, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TextIsStreamedBeforeTheTurnCompletes(bool claude)
    {
        using var http = new HttpClient(new SequenceHandler((_, _) => Finish(claude)));
        var deltas = new List<string>();
        var turn = await new AgentProtocol(http).CompleteAsync(claude ? Opus : new(), "key", "system",
            new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }), deltas.Add, null, CancellationToken.None);
        Assert.Equal("Done", turn.Text);
        Assert.Equal("Done", string.Concat(deltas));
        Assert.True(deltas.Count >= 2);
    }

    [Fact]
    public async Task ProgressThinkingIsReportedAndReplayedWithSignature()
    {
        using var http = new HttpClient(new SequenceHandler((_, _) => JsonSerializer.Serialize(new
        {
            stop_reason = "end_turn",
            content = new object[] { new { type = "thinking", thinking = "Reading the service first", signature = "sig" }, new { type = "text", text = "Done" } },
        })));
        var notes = new List<string>();
        var turn = await new AgentProtocol(http).CompleteAsync(Opus, "key", "system",
            new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }), null, notes.Add, CancellationToken.None);
        Assert.Equal(["Reading the service first"], notes);
        var thinking = turn.Message["content"]![0]!;
        Assert.Equal("thinking", thinking["type"]!.GetValue<string>());
        Assert.Equal("sig", thinking["signature"]!.GetValue<string>());
    }

    [Fact]
    public async Task InterruptedStreamNeverExecutesTools()
    {
        var truncated = Sse.Convert(Tool(true, "write_file", new { path = "A.cs", content = "x", expected_sha256 = "new" }));
        truncated = truncated.Substring(0, truncated.IndexOf("message_delta", StringComparison.Ordinal) - 20);
        var handler = new RawHandler(truncated);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<IOException>(() => new AgentProtocol(http).CompleteAsync(Opus, "key", "system",
            new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }), CancellationToken.None));
    }

    private sealed class RawHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") });
    }

    private static readonly ProviderSettings Opus = new()
        { Provider = ProviderKind.Claude, Endpoint = "https://api.anthropic.com/v1", Model = "claude-opus-5-5", KeyEnvironmentVariable = "ANTHROPIC_API_KEY" };

    private static string Tool(bool claude, string name, object args) => claude
        ? JsonSerializer.Serialize(new { stop_reason = "tool_use", content = new object[] { new { type = "tool_use", id = "tool_id", name, input = args } } })
        : JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "tool_calls", message = new { role = "assistant", content = (string?)null,
            tool_calls = new[] { new { id = "tool_id", type = "function", function = new { name, arguments = JsonSerializer.Serialize(args) } } } } } } });

    private static string Finish(bool claude) => claude
        ? "{\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":\"Done\"}]}"
        : "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"Done\"}}]}";

    private sealed class SequenceHandler(Func<int, JsonNode, string> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        public List<JsonNode> Requests { get; } = [];
        public List<string?> Betas { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            Requests.Add(json.DeepClone());
            Betas.Add(request.Headers.TryGetValues("anthropic-beta", out var values) ? values.Single() : null);
            return new(HttpStatusCode.OK) { Content = new StringContent(Sse.Convert(respond(Count++, json)), Encoding.UTF8, "text/event-stream") };
        }
    }
}

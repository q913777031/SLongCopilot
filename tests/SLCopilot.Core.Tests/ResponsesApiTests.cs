using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

/// <summary>官方 OpenAI 端点上的 GPT-6 系列走 Responses API：请求格式、流解析、加密推理原样回传、截断与失败。</summary>
public sealed class ResponsesApiTests
{
    private static readonly ProviderSettings Astra = new()
        { Provider = ProviderKind.OpenAI, Endpoint = "https://api.openai.com/v1", Model = "gpt-6-astra", OpenAiReasoningEffort = "high", MaxOutputTokens = 64_000 };

    private static readonly JsonObject Reasoning = new() { ["id"] = "rs_1", ["type"] = "reasoning", ["summary"] = new JsonArray(), ["encrypted_content"] = "gAAAA-opaque" };

    private static JsonObject Call(string id, string name, string arguments) => new()
        { ["id"] = "fc_" + id, ["type"] = "function_call", ["status"] = "completed", ["call_id"] = id, ["name"] = name, ["arguments"] = arguments };

    private static JsonObject Message(string text) => new()
    {
        ["id"] = "msg_1", ["type"] = "message", ["role"] = "assistant", ["status"] = "completed",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text, ["annotations"] = new JsonArray() }),
    };

    /// <summary>按 Responses 流式事件输出：每个输出项 added → 增量（文本、参数各拆两段）→ done，最后 response.completed。</summary>
    private static string Stream(IEnumerable<JsonObject> items, bool outputInCompleted = true, string status = "completed", string? terminal = null)
    {
        var sb = new StringBuilder();
        var list = items.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var item = list[i];
            Event(sb, new JsonObject { ["type"] = "response.output_item.added", ["output_index"] = i, ["item"] = item.DeepClone() });
            if ((string?)item["type"] == "message")
                foreach (var piece in Halves((string?)item["content"]![0]!["text"] ?? ""))
                    Event(sb, new JsonObject { ["type"] = "response.output_text.delta", ["output_index"] = i, ["content_index"] = 0, ["delta"] = piece });
            if ((string?)item["type"] == "function_call")
                foreach (var piece in Halves((string?)item["arguments"] ?? ""))
                    Event(sb, new JsonObject { ["type"] = "response.function_call_arguments.delta", ["output_index"] = i, ["delta"] = piece });
            Event(sb, new JsonObject { ["type"] = "response.output_item.done", ["output_index"] = i, ["item"] = item.DeepClone() });
        }
        if (terminal is not null) { sb.Append(terminal); return sb.ToString(); }
        var response = new JsonObject
        {
            ["id"] = "resp_1", ["status"] = status, ["model"] = "gpt-6-astra-2026-09-03",
            ["usage"] = new JsonObject { ["input_tokens"] = 1000, ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 800 }, ["output_tokens"] = 50 },
        };
        if (outputInCompleted) response["output"] = new JsonArray(list.Select(i => (JsonNode)i.DeepClone()).ToArray());
        Event(sb, new JsonObject { ["type"] = "response.completed", ["response"] = response });
        return sb.ToString();
    }

    private static void Event(StringBuilder sb, JsonObject data) => sb.Append("event: ").Append((string?)data["type"]).Append("\ndata: ").Append(data.ToJsonString()).Append("\n\n");

    private static IEnumerable<string> Halves(string text) => text.Length < 2 ? [text] : [text.Substring(0, text.Length / 2), text.Substring(text.Length / 2)];

    private static JsonArray User(string text) => new(new JsonObject { ["role"] = "user", ["content"] = text });

    [Fact]
    public void OnlyGpt6OnTheOfficialEndpointUsesResponses()
    {
        Assert.True(Astra.UsesResponsesApi);
        Assert.True((Astra with { Model = "gpt-6.1-sol" }).UsesResponsesApi);
        Assert.True((Astra with { Model = "gpt-6-astra-2026-09-03" }).UsesResponsesApi);
        Assert.False((Astra with { Model = "gpt-4.1" }).UsesResponsesApi);
        Assert.False((Astra with { Endpoint = "https://gateway.example.com/v1" }).UsesResponsesApi);
        Assert.False((Astra with { Provider = ProviderKind.Claude }).UsesResponsesApi);
        Assert.Equal("https://api.openai.com/v1/responses", Astra.ResponsesUri().ToString());
        Assert.Equal("https://api.openai.com/v1/chat/completions", Astra.RequestUri().ToString());
    }

    [Fact]
    public void Gpt6EffortLevelsLabelsAndLimits()
    {
        Assert.DoesNotContain("none", ModelNames.OpenAiEffortLevels("gpt-6-astra"));
        Assert.DoesNotContain("none", ModelNames.OpenAiEffortLevels("gpt-6.1-sol"));
        Assert.Contains("max", ModelNames.OpenAiEffortLevels("gpt-6.1-sol"));
        Assert.Contains("none", ModelNames.OpenAiEffortLevels("gpt-6-luna"));
        Assert.Equal(ProviderSettings.ReasoningEffortLevels, ModelNames.OpenAiEffortLevels("gpt-4.1"));
        Assert.Throws<ArgumentException>(() => (Astra with { OpenAiReasoningEffort = "none" }).RequestUri());
        (Astra with { Model = "gpt-6-luna", OpenAiReasoningEffort = "none" }).RequestUri();

        Assert.Equal("GPT-6.1 Sol", ModelNames.Label("gpt-6.1-sol"));
        Assert.Equal("GPT-6 Astra", ModelNames.Label("gpt-6-astra"));
        Assert.Equal("gpt-4.1-mini", ModelNames.Label("gpt-4.1-mini"));
        Assert.Equal("gpt-6-astra-2026-09-03", ModelNames.Label("gpt-6-astra-2026-09-03"));
        Assert.Equal(ModelLimits.Gpt6ContextWindow, ModelLimits.KnownContextWindow("gpt-6.1-terra"));
        Assert.Equal(64_000, Astra.OutputTokenLimit());
        Assert.Equal(ProviderSettings.OpenAiMaxOutputTokens, (Astra with { Model = "gpt-4.1" }).OutputTokenLimit());
    }

    [Fact]
    public async Task RequestIsStatelessWithFlatToolsAndEncryptedReasoning()
    {
        var handler = new Handler((_, _) => Stream([Message("hi")]));
        using var http = new HttpClient(handler);
        var turn = await new AgentProtocol(http).CompleteAsync(Astra, "key", "system prompt", User("hello"), CancellationToken.None);
        Assert.Equal("https://api.openai.com/v1/responses", handler.Uris[0]);
        var request = handler.Requests[0];
        Assert.Equal("gpt-6-astra", (string?)request["model"]);
        Assert.Equal("system prompt", (string?)request["instructions"]);
        Assert.False((bool)request["store"]!);
        Assert.True((bool)request["stream"]!);
        Assert.Equal("reasoning.encrypted_content", (string?)request["include"]![0]);
        Assert.Equal("high", (string?)request["reasoning"]!["effort"]);
        Assert.Equal(64_000, (int)request["max_output_tokens"]!);
        Assert.Equal("hello", (string?)request["input"]![0]!["content"]);
        Assert.Null(request["messages"]);
        var tool = request["tools"]!.AsArray().Single(t => (string?)t!["name"] == "read_file")!;
        Assert.Equal("function", (string?)tool["type"]);
        Assert.False((bool)tool["strict"]!);
        Assert.Null(tool["function"]);
        Assert.Equal("object", (string?)tool["parameters"]!["type"]);
        Assert.Equal("hi", turn.Text);
        Assert.Equal(new TokenUsage(200, 50, 800, 0), turn.Usage);
        Assert.Equal("gpt-6-astra-2026-09-03", turn.ServedModel);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OutputItemsAreKeptInOrderIncludingEncryptedReasoning(bool outputInCompleted)
    {
        using var http = new HttpClient(new Handler((_, _) => Stream([(JsonObject)Reasoning.DeepClone(), Call("call_a", "read_file", "{\"path\":\"A.cs\"}")], outputInCompleted)));
        var deltas = new List<int>();
        var turn = await new AgentProtocol(http).CompleteAsync(Astra, "key", "s", User("go"), null, null, CancellationToken.None, onToolInput: (_, chars) => deltas.Add(chars));
        var call = Assert.Single(turn.Calls);
        Assert.Equal("call_a", call.Id);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("A.cs", call.Arguments.GetProperty("path").GetString());
        Assert.NotEmpty(deltas);
        var history = User("go");
        AgentProtocol.AppendTurn(history, turn);
        AgentProtocol.AppendResults(history, false, [(call, new ToolResult("content", false))], responses: true);
        Assert.Equal(new[] { null, "reasoning", "function_call", "function_call_output" }, history.Select(m => (string?)m!["type"]));
        Assert.Equal("gAAAA-opaque", (string?)history[1]!["encrypted_content"]);
        Assert.Equal("rs_1", (string?)history[1]!["id"]);
        Assert.Equal("call_a", (string?)history[3]!["call_id"]);
        Assert.Equal("content", (string?)history[3]!["output"]);
    }

    [Fact]
    public async Task IncompleteFailedAndCutStreamsNeverExecuteTools()
    {
        var items = new[] { Call("c", "write_file", "{\"path\":\"A.cs\",\"content\":\"x\",\"expected_sha256\":\"new\"}") };
        var incomplete = "data: {\"type\":\"response.incomplete\",\"response\":{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"}}}\n\n";
        var failed = "data: {\"type\":\"response.failed\",\"response\":{\"status\":\"failed\",\"error\":{\"code\":\"server_error\"}}}\n\n";
        foreach (var body in new[] { Stream(items, terminal: incomplete), Stream(items, terminal: failed), Stream(items, terminal: ""), Stream(items, status: "incomplete") })
        {
            using var http = new HttpClient(new Handler((_, _) => body));
            await Assert.ThrowsAsync<IOException>(() => new AgentProtocol(http).CompleteAsync(Astra, "key", "s", User("go"), CancellationToken.None));
        }
    }

    [Fact]
    public async Task RefusalIsAnError()
    {
        var refusal = new JsonObject
        {
            ["id"] = "msg_1", ["type"] = "message", ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "refusal", ["refusal"] = "no" }),
        };
        using var http = new HttpClient(new Handler((_, _) => Stream([refusal])));
        await Assert.ThrowsAsync<IOException>(() => new AgentProtocol(http).CompleteAsync(Astra, "key", "s", User("go"), CancellationToken.None));
    }

    [Fact]
    public async Task RunnerReplaysReasoningAndToolOutputsInTheNextRequest()
    {
        var temp = Path.Combine(Path.GetTempPath(), "SLCopilot-responses-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, "project"));
        try
        {
            var handler = new Handler((n, _) => n == 0
                ? Stream([(JsonObject)Reasoning.DeepClone(), Call("call_p", "update_plan", "{\"plan\":\"doing: check\"}")])
                : Stream([Message("done")]));
            using var http = new HttpClient(handler);
            var workspace = new WorkspaceTools(Path.Combine(temp, "project"), true, Path.Combine(temp, "backups"));
            var conversation = new AgentConversation();
            await new AgentRunner(new AgentProtocol(http)).RunAsync(Astra, "key", "Plan it", workspace, _ => { }, CancellationToken.None, conversation: conversation);
            var second = handler.Requests[1]["input"]!.AsArray();
            Assert.Equal(new[] { null, "reasoning", "function_call", "function_call_output" }, second.Select(m => (string?)m!["type"]));
            Assert.Equal("gAAAA-opaque", (string?)second[1]!["encrypted_content"]);
            Assert.Equal("call_p", (string?)second[3]!["call_id"]);
            Assert.Equal("message", (string?)conversation.Export().Last()!["type"]);
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public async Task RejectedReasoningIsDroppedOnceAndRetried()
    {
        var handler = new Handler((n, request) => n == 0 ? Stream([(JsonObject)Reasoning.DeepClone(), Call("call_p", "update_plan", "{\"plan\":\"doing: a\"}")])
            : request["input"]!.AsArray().Any(i => (string?)i!["type"] == "reasoning") ? null : Stream([Message("ok")]));
        var temp = Path.Combine(Path.GetTempPath(), "SLCopilot-responses-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, "project"));
        try
        {
            using var http = new HttpClient(handler);
            var events = new List<AgentEvent>();
            await new AgentRunner(new AgentProtocol(http)).RunAsync(Astra, "key", "go", new WorkspaceTools(Path.Combine(temp, "project"), true, Path.Combine(temp, "backups")),
                events.Add, CancellationToken.None);
            Assert.Equal(3, handler.Requests.Count);
            Assert.Contains(events, e => e.Kind == "thinking_stripped");
            Assert.DoesNotContain(handler.Requests[2]["input"]!.AsArray(), i => (string?)i!["type"] == "reasoning");
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public void ResponsesItemsAppearInCompactionTranscripts()
    {
        var history = User("fix it");
        history.Add(Reasoning.DeepClone());
        history.Add(Call("c", "read_file", "{\"path\":\"A.cs\"}"));
        history.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = "c", ["output"] = "file text" });
        history.Add(Message("fixed"));
        var transcript = ContextCompactor.AgentTranscript(history);
        Assert.Contains("Tool call: read_file", transcript);
        Assert.Contains("Tool result: file text", transcript);
        Assert.Contains("Assistant: fixed", transcript);
        Assert.DoesNotContain("opaque", transcript);
    }

    [Fact]
    public void ResponsesImagesUseInputParts()
    {
        var content = ImageContent.UserContent(false, "what is this?", [new ImageAttachment("image/png", [1, 2, 3], 1, 1, "a.png")], responses: true).AsArray();
        Assert.Equal("input_text", (string?)content[0]!["type"]);
        Assert.Equal("input_image", (string?)content[1]!["type"]);
        Assert.StartsWith("data:image/png;base64,", (string?)content[1]!["image_url"]);
        var stripped = ImageContent.WithoutImageData(new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content.DeepClone() }));
        Assert.Equal("input_text", (string?)stripped[0]!["content"]![1]!["type"]);
        Assert.True(new AgentConversation(new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content.DeepClone() }), false).HasUserMessageContaining("what is"));
    }

    /// <summary>按请求序号返回 SSE 文本；返回 null 时回复 HTTP 400。</summary>
    private sealed class Handler(Func<int, JsonNode, string?> respond) : HttpMessageHandler
    {
        public List<JsonNode> Requests { get; } = [];
        public List<string> Uris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Requests.Add(json.DeepClone());
            Uris.Add(request.RequestUri!.ToString());
            var body = respond(Requests.Count - 1, json);
            return body is null
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"invalid reasoning item\"}}", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
        }
    }
}

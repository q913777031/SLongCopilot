using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SLCopilot.Core;

public sealed record ToolCall(string Id, string Name, JsonElement Arguments);
/// <param name="ServedModel">服务端响应中的模型 ID；未返回时为 null。</param>
/// <param name="Items">OpenAI Responses API 的全部输出项（含加密推理与 function_call），按顺序原样写回历史；为 null 时历史写回 <paramref name="Message"/>。</param>
public sealed record AgentTurn(string Text, IReadOnlyList<ToolCall> Calls, JsonObject Message, TokenUsage? Usage = null, string? ServedModel = null,
    IReadOnlyList<JsonObject>? Items = null);
public sealed record ToolResult(string Content, bool IsError = false)
{
    /// <summary>只给界面显示的附加内容（如写入的差异），不发送给模型；没有时为 null。</summary>
    public string? Display { get; init; }
}

/// <summary>
/// 带原生工具调用的流式模型回合。文本片段经 <c>onText</c> 实时回调，但工具只在整条响应完整、停止原因可接受后才执行：
/// 截断、拒答、流中断、停止原因与工具调用不匹配一律抛出 IOException，调用方不得执行其中的工具。
/// 返回的 <see cref="AgentTurn.Message"/> 可原样写回历史（Claude 的 thinking 块含签名）。
/// </summary>
/// <param name="onRetry">暂时性失败重试前的回调（第几次重试、等待时间），可在任意线程调用。</param>
public sealed class AgentProtocol(HttpClient http, Action<int, TimeSpan>? onRetry = null)
{
    private const int MaxResponseChars = 1_000_000;

    /// <summary>附加在内置工具之后的 MCP 工具；在一次对话内必须保持不变（Claude 5 要求工具定义不变）。</summary>
    public IReadOnlyList<McpTool> ExternalTools { get; init; } = [];
    /// <summary>单个回合内最多的工具调用数（模型可在一个回合里并行读取或修改多个文件）。</summary>
    private const int MaxToolCalls = 24;

    /// <summary>
    /// 按需服务端压缩（官方 Claude 端点）：发送与对话相同的 system、工具与完整历史，并带 <c>compaction: {type: summarize}</c>；
    /// 返回服务端生成的 compaction 块（含签名，必须原样回传）。服务端摘要不算改动历史，原有 thinking 块保持有效。
    /// <c>stop_reason</c> 不是 <c>compaction</c>（拒答、超出上下文、调用了工具等）时抛出 IOException，历史保持不变。
    /// </summary>
    public async Task<JsonObject> RequestCompactionAsync(ProviderSettings settings, string key, string system, JsonArray messages, CancellationToken token)
    {
        var payload = new JsonObject
        {
            ["model"] = settings.Model, ["system"] = system, ["messages"] = messages.DeepClone(), ["tools"] = ToolDefinitions.Create(claude: true, ExternalTools),
            ["compaction"] = new JsonObject { ["type"] = "summarize", ["instructions"] = ContextCompactor.ServerInstructions },
        };
        ClaudeRequest.Apply(payload, settings, progressUpdates: false, fallbacks: false);
        var body = payload.ToJsonString();
        var uri = settings.RequestUri();
        using var response = await ProviderRetry.SendAsync(http, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            ClaudeRequest.AddHeaders(request, settings, key, fallbacks: false, extraBetas: [ClaudeRequest.CompactionBeta]);
            return request;
        }, onRetry, token);
        var text = await Compat.ReadAsStringAsync(response.Content, token);
        var root = JsonNode.Parse(text) as JsonObject ?? throw new IOException("Malformed compaction response.");
        var stop = (string?)root["stop_reason"];
        if (stop != "compaction" || root["content"] is not JsonArray { Count: 1 } content || content[0] is not JsonObject block || (string?)block["type"] != "compaction")
            throw new IOException($"The server did not return a summary (stop_reason: {stop ?? "none"}).");
        return (JsonObject)block.DeepClone();
    }

    /// <summary>把较早（最近 <see cref="KeepRecentToolResults"/> 个之外）的长工具结果替换为说明文字；只用于 OpenAI 线格式（role = tool）。</summary>
    internal static void TrimOldToolResults(JsonArray messages)
    {
        var results = messages.OfType<JsonObject>().Where(m => (string?)m["role"] == "tool").ToList();
        foreach (var message in results.Take(Math.Max(0, results.Count - KeepRecentToolResults)))
            if (message["content"] is JsonValue value && (string?)value is { Length: > TrimmableResultChars } content)
                message["content"] = $"[Earlier tool result omitted to save tokens ({content.Length:N0} characters). Call the tool again if you need it.]";
    }

    /// <summary>历史中是否有 thinking / redacted_thinking 块。</summary>
    public static bool ContainsThinking(JsonArray messages) => messages.OfType<JsonObject>()
        .Any(m => m["content"] is JsonArray blocks && blocks.OfType<JsonObject>().Any(b => (string?)b["type"] is "thinking" or "redacted_thinking"));

    /// <summary>
    /// 返回去掉全部 thinking / redacted_thinking 块的历史副本；文本与工具调用保持不变。只剩 thinking 的 assistant 消息改为一条占位文本，保持消息交替。
    /// </summary>
    public static JsonArray StripThinking(JsonArray messages)
    {
        var result = new JsonArray();
        foreach (var node in messages)
        {
            var message = (JsonObject)node!.DeepClone();
            if (message["content"] is JsonArray blocks)
            {
                var kept = new JsonArray();
                foreach (var block in blocks)
                    if ((string?)block?["type"] is not ("thinking" or "redacted_thinking")) kept.Add(block!.DeepClone());
                if (kept.Count == 0 && (string?)message["role"] == "assistant") kept.Add(new JsonObject { ["type"] = "text", ["text"] = "(no visible reply)" });
                message["content"] = kept;
            }
            result.Add(message);
        }
        return result;
    }

    /// <summary>用同一 HttpClient 生成上下文摘要（聊天接口，无工具、无 thinking）。</summary>
    public Task<string> SummarizeAsync(ProviderSettings settings, string key, string transcript, CancellationToken token)
        => ContextCompactor.SummarizeAsync(new AiClient(http, onRetry), settings, key, transcript, token);

    public Task<AgentTurn> CompleteAsync(ProviderSettings settings, string key, string system, JsonArray messages, CancellationToken token)
        => CompleteAsync(settings, key, system, messages, null, null, token);

    /// <summary>OpenAI 兼容服务裁剪旧工具结果时保留的最近工具结果数，以及短于该字符数的结果不裁剪。</summary>
    private const int KeepRecentToolResults = 3, TrimmableResultChars = 2000;

    /// <param name="onText">收到回答文本片段时在读取线程上同步回调。</param>
    /// <param name="onThinking">每个 thinking 块结束时回调其可见文本（官方端点的进度说明）；为空文本时不回调。</param>
    /// <param name="onToolInput">工具调用参数流式到达时在读取线程上回调（工具名、目前已收到的参数字符数），用于显示“正在生成 write_file …”。</param>
    /// <param name="trimOldToolResults">
    /// 只用于 OpenAI 兼容服务：发送时把较早（最近 3 个之外）且超过 2000 字符的工具结果替换为简短说明，节约 token。只改发送的副本，不改历史。
    /// Claude 端点不使用（改动历史会使思考记录失效；官方端点由服务端上下文编辑完成）。
    /// </param>
    public async Task<AgentTurn> CompleteAsync(ProviderSettings settings, string key, string system, JsonArray messages,
        Action<string>? onText, Action<string>? onThinking, CancellationToken token, bool trimOldToolResults = false, Action<string, int>? onToolInput = null)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("API key is missing.");
        if (settings.UsesResponsesApi) return await CompleteResponsesAsync(settings, key, system, messages, onText, onToolInput, token).ConfigureAwait(false);
        var claude = settings.Provider == ProviderKind.Claude;
        var wireMessages = (JsonArray)messages.DeepClone();
        if (!claude && trimOldToolResults) TrimOldToolResults(wireMessages);
        if (settings.IsDeepSeek)
            foreach (var historical in wireMessages.OfType<JsonObject>())
                if ((string?)historical["role"] == "assistant" && historical["reasoning_content"] is null)
                    historical["reasoning_content"] = ""; // Local summaries and pre-upgrade history have no reasoning.
        if (!claude) wireMessages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = system });
        var payload = new JsonObject
        {
            ["model"] = settings.Model, ["stream"] = true, ["messages"] = wireMessages,
            ["tools"] = ToolDefinitions.Create(claude, ExternalTools),
        };
        if (claude)
        {
            payload["system"] = system;
            // 不使用服务端上下文编辑（clear_tool_uses）：每次扩大清理范围都会使整个提示缓存失效、按 1.25 倍重写，
            // 实测远超清理省下的缓存读取（0.1 倍）。上下文接近窗口时由自动压缩处理。
            ClaudeRequest.Apply(payload, settings, progressUpdates: true);
        }
        else
        {
            OpenAiRequest.Apply(payload, settings);
        }
        // 不在本地限制请求大小：上限是模型上下文窗口，超出时服务端返回 400/413。
        var body = payload.ToJsonString();
        var uri = settings.RequestUri();
        var extraBetas = new List<string>();
        if (claude && ClaudeRequest.HasCompactionBlock(messages)) extraBetas.Add(ClaudeRequest.CompactionBeta);
        var compactionBetas = extraBetas.Count > 0 ? extraBetas.ToArray() : null;
        using var response = await ProviderRetry.SendAsync(http, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (claude) ClaudeRequest.AddHeaders(request, settings, key, progressUpdates: true, extraBetas: compactionBetas);
            else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return request;
        }, onRetry, token);
        // net472 的响应流读取不响应取消令牌：取消时释放响应以中断读取，SseReader 负责转换为取消异常。
        using var abort = token.Register(response.Dispose);
        using var stream = await Compat.ReadAsStreamAsync(response.Content, token);
        using var reader = new StreamReader(stream);
        var usage = new UsageCounter();
        var (text, message, calls) = claude
            ? await ReadClaudeAsync(reader, onText, onThinking, onToolInput, usage, token)
            : await ReadOpenAiAsync(reader, onText, onToolInput, usage, token, settings.IsDeepSeek);
        if (calls.Count > MaxToolCalls || calls.Select(c => c.Id).Distinct().Count() != calls.Count)
            throw new IOException("Invalid number of tools or duplicate tool IDs.");
        return new(text, calls, message, usage.Result, usage.ServedModel);
    }

    /// <summary>
    /// OpenAI Responses API 的一个回合（官方端点上的 GPT-6 系列）：store: false 的无状态请求，instructions 每次发送；
    /// <paramref name="messages"/> 为 Responses 输入项（用户消息、此前各回合的全部输出项——含加密推理与 function_call——原样回传，以及 function_call_output）。
    /// 结果以完整的输出项为准（response.completed 带 output 时用它，否则用各 response.output_item.done 的项），文本增量只用于显示。
    /// 未收到 response.completed、状态不是 completed、incomplete / failed / error 事件，一律抛出 IOException，调用方不得执行其中的工具。
    /// </summary>
    private async Task<AgentTurn> CompleteResponsesAsync(ProviderSettings settings, string key, string system, JsonArray messages,
        Action<string>? onText, Action<string, int>? onToolInput, CancellationToken token)
    {
        var payload = new JsonObject
        {
            ["model"] = settings.Model, ["instructions"] = system, ["input"] = messages.DeepClone(),
            ["tools"] = ToolDefinitions.CreateResponses(ExternalTools), ["stream"] = true,
            // 不在 OpenAI 保存响应；推理以加密形式随输出返回，由下一次请求原样回传。
            ["store"] = false, ["include"] = new JsonArray("reasoning.encrypted_content"),
            ["max_output_tokens"] = settings.OutputTokenLimit(),
        };
        if (!string.IsNullOrEmpty(settings.OpenAiReasoningEffort)) payload["reasoning"] = new JsonObject { ["effort"] = settings.OpenAiReasoningEffort };
        var body = payload.ToJsonString();
        var uri = settings.ResponsesUri();
        using var response = await ProviderRetry.SendAsync(http, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return request;
        }, onRetry, token);
        using var abort = token.Register(response.Dispose);
        using var stream = await Compat.ReadAsStreamAsync(response.Content, token);
        using var reader = new StreamReader(stream);
        var usage = new UsageCounter();
        var (text, items, calls) = await ReadResponsesAsync(reader, onText, onToolInput, usage, token);
        if (calls.Count > MaxToolCalls || calls.Select(c => c.Id).Distinct().Count() != calls.Count)
            throw new IOException("Invalid number of tools or duplicate tool IDs.");
        return new(text, calls, new JsonObject { ["role"] = "assistant", ["content"] = text }, usage.Result, usage.ServedModel, items);
    }

    private static async Task<(string Text, List<JsonObject> Items, List<ToolCall> Calls)> ReadResponsesAsync(
        TextReader reader, Action<string>? onText, Action<string, int>? onToolInput, UsageCounter usage, CancellationToken token)
    {
        var done = new SortedDictionary<int, JsonObject>();
        var names = new Dictionary<int, string>();
        var argumentChars = new Dictionary<int, int>();
        var received = 0;
        await foreach (var data in SseReader.ReadAsync(reader, token, MaxResponseChars))
        {
            var root = JsonNode.Parse(data) as JsonObject ?? throw new IOException("Malformed stream event.");
            var index = root["output_index"] is JsonValue position && position.TryGetValue<int>(out var at) ? at : done.Count;
            switch ((string?)root["type"])
            {
                case "response.output_text.delta":
                    if ((string?)root["delta"] is { Length: > 0 } piece)
                    {
                        received += piece.Length;
                        onText?.Invoke(piece);
                    }
                    break;
                case "response.output_item.added":
                    if ((string?)root["item"]?["type"] == "function_call" && (string?)root["item"]?["name"] is { } name) names[index] = name;
                    break;
                case "response.function_call_arguments.delta":
                    if ((string?)root["delta"] is { Length: > 0 } args)
                    {
                        received += args.Length;
                        argumentChars[index] = (argumentChars.TryGetValue(index, out var sofar) ? sofar : 0) + args.Length;
                        if (names.TryGetValue(index, out var tool)) onToolInput?.Invoke(tool, argumentChars[index]);
                    }
                    break;
                case "response.output_item.done":
                    done[index] = root["item"] as JsonObject is { } item ? (JsonObject)item.DeepClone() : throw new IOException("Malformed output item.");
                    break;
                case "response.completed":
                {
                    var result = root["response"] as JsonObject;
                    if (result?["status"] is JsonValue status && (string?)status != "completed")
                        throw new IOException($"Agent response was not completed (status: {(string?)status}).");
                    usage.ReadResponses(result?["usage"]);
                    if (result?["model"] is JsonValue served && served.TryGetValue<string>(out var servedModel)) usage.ServedModel = servedModel;
                    var items = result?["output"] is JsonArray { Count: > 0 } output
                        ? output.Select(o => o as JsonObject ?? throw new IOException("Malformed output item.")).Select(o => (JsonObject)o.DeepClone()).ToList()
                        : done.Values.ToList();
                    var (text, calls) = ParseResponsesOutput(items);
                    return (text, items, calls);
                }
                case "response.incomplete":
                    throw new IOException($"Agent response was incomplete ({(string?)root["response"]?["incomplete_details"]?["reason"] ?? "unknown reason"}). Partial output was discarded.");
                case "response.failed":
                    throw new IOException($"Provider reported a failed response ({(string?)root["response"]?["error"]?["code"] ?? "unknown error"}). Retry the request.");
                case "error":
                    throw new IOException("Provider reported a streaming error. Retry the request.");
            }
            if (received > MaxResponseChars) throw new IOException("Agent response is too large.");
        }
        throw new IOException("Provider stream ended before its completion marker. Partial output was discarded.");
    }

    /// <summary>从 Responses 输出项取回答文本（message 的 output_text）与工具调用（function_call）；拒答时抛出 IOException。</summary>
    private static (string Text, List<ToolCall> Calls) ParseResponsesOutput(IEnumerable<JsonObject> items)
    {
        var text = new StringBuilder();
        var calls = new List<ToolCall>();
        foreach (var item in items)
            switch ((string?)item["type"])
            {
                case "message":
                    foreach (var part in (item["content"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        if ((string?)part["type"] == "refusal") throw new IOException("Provider refused the request.");
                        if ((string?)part["type"] == "output_text") text.Append((string?)part["text"]);
                    }
                    break;
                case "function_call":
                    calls.Add(ParseCall(item["call_id"], item["name"], (string?)item["arguments"]));
                    break;
            }
        return (text.ToString(), calls);
    }

    /// <summary>把模型的一个回合写回历史：Responses API 写回全部输出项（按顺序、原样，含加密推理），其他服务写回一条 assistant 消息。</summary>
    public static void AppendTurn(JsonArray messages, AgentTurn turn)
    {
        if (turn.Items is { } items)
            foreach (var item in items) messages.Add(item.DeepClone());
        else messages.Add(turn.Message);
    }

    /// <summary>历史中是否有 Responses API 的推理项。</summary>
    public static bool ContainsReasoningItems(JsonArray messages) => messages.OfType<JsonObject>().Any(m => (string?)m["type"] == "reasoning");

    /// <summary>返回去掉全部 Responses 推理项的历史副本（推理项无法被当前模型使用时的一次性恢复）；其他项不变。</summary>
    public static JsonArray StripReasoningItems(JsonArray messages)
        => new(messages.OfType<JsonObject>().Where(m => (string?)m["type"] != "reasoning").Select(m => (JsonNode)m.DeepClone()).ToArray());

    private sealed class BlockBuilder
    {
        public string Type = "";
        public JsonObject Start = new();
        public readonly StringBuilder Text = new();
        public readonly StringBuilder Signature = new();
    }

    private static async Task<(string Text, JsonObject Message, List<ToolCall> Calls)> ReadClaudeAsync(
        TextReader reader, Action<string>? onText, Action<string>? onThinking, Action<string, int>? onToolInput, UsageCounter usage, CancellationToken token)
    {
        var blocks = new SortedDictionary<int, BlockBuilder>();
        string? stopReason = null;
        var received = 0;
        await foreach (var data in SseReader.ReadAsync(reader, token, MaxResponseChars))
        {
            var root = JsonNode.Parse(data) as JsonObject ?? throw new IOException("Malformed stream event.");
            switch ((string?)root["type"])
            {
                case "error":
                    throw new IOException("Provider reported a streaming error. Retry the request.");
                case "message_start":
                    usage.ReadClaude(root["message"]?["usage"]);
                    usage.ServedModel = (string?)root["message"]?["model"];
                    break;
                case "content_block_start":
                {
                    var index = (int)root["index"]!;
                    var start = root["content_block"] as JsonObject ?? throw new IOException("Malformed content block.");
                    var blockType = (string?)start["type"] ?? "";
                    if (blockType == "fallback")
                    {
                        // 服务端切换到备用模型：此前的非文本块（thinking、tool_use）不得回传。
                        foreach (var key in blocks.Where(b => b.Value.Type != "text").Select(b => b.Key).ToList()) blocks.Remove(key);
                        break;
                    }
                    var builder = new BlockBuilder { Type = blockType, Start = (JsonObject)start.DeepClone() };
                    if (blockType == "text" && (string?)start["text"] is { Length: > 0 } initial) { builder.Text.Append(initial); onText?.Invoke(initial); }
                    blocks[index] = builder;
                    break;
                }
                case "content_block_delta":
                {
                    if (!blocks.TryGetValue((int)root["index"]!, out var builder)) break;
                    var delta = root["delta"] as JsonObject;
                    string? piece = null;
                    switch ((string?)delta?["type"])
                    {
                        case "text_delta": piece = (string?)delta!["text"]; if (piece is { Length: > 0 }) onText?.Invoke(piece); break;
                        case "input_json_delta": piece = (string?)delta!["partial_json"]; break;
                        case "thinking_delta": piece = (string?)delta!["thinking"]; break;
                        case "signature_delta": builder.Signature.Append((string?)delta!["signature"]); break;
                    }
                    if (piece is null) break;
                    builder.Text.Append(piece);
                    received += piece.Length;
                    if (builder.Type == "tool_use") onToolInput?.Invoke((string?)builder.Start["name"] ?? "", builder.Text.Length);
                    if (received > MaxResponseChars) throw new IOException("Agent response is too large.");
                    break;
                }
                case "content_block_stop":
                    if (blocks.TryGetValue((int)root["index"]!, out var stopped) && stopped.Type == "thinking" && stopped.Text.Length > 0)
                        onThinking?.Invoke(stopped.Text.ToString());
                    break;
                case "message_delta":
                    if (root["delta"]?["stop_reason"] is JsonValue reason) stopReason = (string?)reason;
                    usage.ReadClaude(root["usage"]);
                    break;
                case "message_stop":
                {
                    if (stopReason is not ("end_turn" or "tool_use")) throw new IOException("Agent response was incomplete or refused.");
                    var content = new JsonArray();
                    var text = new StringBuilder();
                    var calls = new List<ToolCall>();
                    foreach (var builder in blocks.Values)
                    {
                        switch (builder.Type)
                        {
                            case "text":
                                if (builder.Text.Length == 0) break;
                                text.Append(builder.Text);
                                content.Add(new JsonObject { ["type"] = "text", ["text"] = builder.Text.ToString() });
                                break;
                            case "tool_use":
                                var input = builder.Text.Length > 0 ? builder.Text.ToString() : builder.Start["input"]?.ToJsonString() ?? "{}";
                                var call = ParseCall(builder.Start["id"], builder.Start["name"], input);
                                calls.Add(call);
                                content.Add(new JsonObject
                                {
                                    ["type"] = "tool_use", ["id"] = call.Id, ["name"] = call.Name,
                                    ["input"] = JsonNode.Parse(call.Arguments.GetRawText()),
                                });
                                break;
                            case "thinking":
                                content.Add(new JsonObject
                                {
                                    ["type"] = "thinking", ["thinking"] = builder.Text.ToString(), ["signature"] = builder.Signature.ToString(),
                                });
                                break;
                            default:
                                content.Add(builder.Start.DeepClone());
                                break;
                        }
                    }
                    if ((stopReason == "tool_use") != (calls.Count > 0)) throw new IOException("Invalid tool-use response.");
                    return (text.ToString(), new JsonObject { ["role"] = "assistant", ["content"] = content }, calls);
                }
            }
        }
        throw new IOException("Provider stream ended before its completion marker. Partial output was discarded.");
    }

    private static async Task<(string Text, JsonObject Message, List<ToolCall> Calls)> ReadOpenAiAsync(
        TextReader reader, Action<string>? onText, Action<string, int>? onToolInput, UsageCounter usage, CancellationToken token, bool preserveReasoning)
    {
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var pending = new SortedDictionary<int, (StringBuilder Id, StringBuilder Name, StringBuilder Args)>();
        string? finish = null;
        var received = 0;
        await foreach (var data in SseReader.ReadAsync(reader, token, MaxResponseChars))
        {
            if (data == "[DONE]")
            {
                if (finish is not ("stop" or "tool_calls")) throw new IOException("Agent response was incomplete or filtered.");
                var calls = new List<ToolCall>();
                var toolCalls = new JsonArray();
                foreach (var entry in pending.Values)
                {
                    var call = ParseCall(JsonValue.Create(entry.Id.ToString()), JsonValue.Create(entry.Name.ToString()), entry.Args.Length == 0 ? "{}" : entry.Args.ToString());
                    calls.Add(call);
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = call.Id, ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments.GetRawText() },
                    });
                }
                if ((finish == "tool_calls") != (calls.Count > 0)) throw new IOException("Invalid tool-call response.");
                var message = new JsonObject { ["role"] = "assistant", ["content"] = text.Length == 0 ? null : text.ToString() };
                if (preserveReasoning) message["reasoning_content"] = reasoning.ToString();
                if (toolCalls.Count > 0) message["tool_calls"] = toolCalls;
                return (text.ToString(), message, calls);
            }
            var root = JsonNode.Parse(data) as JsonObject ?? throw new IOException("Malformed stream event.");
            if (root["error"] is not null) throw new IOException("Provider reported a streaming error. Retry the request.");
            usage.ReadOpenAi(root["usage"]);
            if (root["model"] is JsonValue served && served.TryGetValue<string>(out var servedModel)) usage.ServedModel = servedModel;
            if (root["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice) continue;
            if (choice["finish_reason"] is JsonValue finishValue && (string?)finishValue is { } reason)
            {
                if (reason is "length" or "content_filter") throw new IOException("Agent response was incomplete or filtered.");
                finish = reason;
            }
            if (choice["delta"] is not JsonObject delta) continue;
            if (preserveReasoning && delta["reasoning_content"] is JsonValue thinking && (string?)thinking is { } thought)
            {
                reasoning.Append(thought);
                received += thought.Length;
            }
            if (delta["refusal"] is JsonValue refusal && (string?)refusal is { Length: > 0 }) throw new IOException("Provider refused the request.");
            if (delta["content"] is JsonValue contentValue && (string?)contentValue is { Length: > 0 } piece)
            {
                text.Append(piece);
                received += piece.Length;
                onText?.Invoke(piece);
            }
            if (delta["tool_calls"] is JsonArray deltas)
                foreach (var node in deltas.OfType<JsonObject>())
                {
                    var index = (int?)node["index"] ?? 0;
                    if (!pending.TryGetValue(index, out var entry)) pending[index] = entry = (new(), new(), new());
                    if (node["type"] is JsonValue typeValue && (string?)typeValue is { } type && type != "function") throw new IOException("Unsupported tool type.");
                    entry.Id.Append((string?)node["id"]);
                    entry.Name.Append((string?)node["function"]?["name"]);
                    var args = (string?)node["function"]?["arguments"];
                    entry.Args.Append(args);
                    received += args?.Length ?? 0;
                    if (args is { Length: > 0 }) onToolInput?.Invoke(entry.Name.ToString(), entry.Args.Length);
                }
            if (received > MaxResponseChars) throw new IOException("Agent response is too large.");
        }
        throw new IOException("Provider stream ended before its completion marker. Partial output was discarded.");
    }

    private static ToolCall ParseCall(JsonNode? id, JsonNode? name, string? arguments)
    {
        var callId = id?.GetValue<string>();
        var toolName = name?.GetValue<string>();
        if (callId is null || toolName is null || string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(toolName)) throw new IOException("Invalid tool call.");
        JsonDocument json;
        try { json = JsonDocument.Parse(arguments ?? "{}"); }
        catch (JsonException) { throw new IOException("Tool arguments were not valid JSON."); }
        using (json)
        {
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("Tool arguments must be an object.");
            return new(callId, toolName, json.RootElement.Clone());
        }
    }

    /// <summary>
    /// 在刚追加的工具结果之后加入一段用户文字（运行中用户补充的要求）。Claude 放进同一条用户消息（工具结果块之后的文字块）；
    /// OpenAI 兼容格式追加一条用户消息。
    /// </summary>
    public static void AppendUserText(JsonArray messages, bool claude, string text)
    {
        if (claude && messages.Count > 0 && messages[messages.Count - 1] is JsonObject { } last && (string?)last["role"] == "user" && last["content"] is JsonArray blocks)
            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        else messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });
    }

    /// <param name="responses">历史为 OpenAI Responses API 输入项：结果写为 function_call_output。</param>
    public static void AppendResults(JsonArray messages, bool claude, IReadOnlyList<(ToolCall Call, ToolResult Result)> results, bool responses = false)
    {
        if (claude)
        {
            var blocks = new JsonArray();
            foreach (var (call, result) in results)
                blocks.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = call.Id,
                    ["content"] = result.Content, ["is_error"] = result.IsError });
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = blocks });
        }
        else if (responses)
            foreach (var (call, result) in results)
                messages.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = call.Id, ["output"] = result.Content });
        else foreach (var (call, result) in results)
            messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.Id, ["content"] = result.Content });
    }
}

internal static class ToolDefinitions
{
    /// <summary>
    /// Responses API 的函数工具：扁平的 {type, name, description, parameters}。显式 strict: false——工具参数含可选字段，
    /// 不满足严格模式“全部字段必填”的要求（省略时服务端会尝试按严格模式规范化）。
    /// </summary>
    public static JsonArray CreateResponses(IReadOnlyList<McpTool>? external = null)
    {
        var tools = new JsonArray();
        foreach (var node in Create(claude: false, external))
        {
            var function = (JsonObject)node!["function"]!;
            tools.Add(new JsonObject
            {
                ["type"] = "function", ["name"] = function["name"]!.DeepClone(), ["description"] = function["description"]!.DeepClone(),
                ["parameters"] = function["parameters"]!.DeepClone(), ["strict"] = false,
            });
        }
        return tools;
    }

    /// <param name="external">MCP 工具：按服务器给出的 inputSchema 原样声明，排在内置工具之后。</param>
    public static JsonArray Create(bool claude, IReadOnlyList<McpTool>? external = null)
    {
        var tools = new JsonArray();
        Add("list_directory", "List a workspace directory (at most 1000 entries, directories first). depth 1 lists immediate children; up to 4 lists the tree below. Use '.' for root. Results may be truncated.",
            ("path", "Relative directory path"), ("depth?:integer", "Levels to list, 1-4 (optional, default 1)"));
        Add("search_files", "Recursively search text source files line by line. query is literal case-insensitive text, or a .NET regular expression when regex is true (case-insensitive, matched per line). " +
            "glob limits the files searched, e.g. *.cs or src/**/*.xaml. Returns at most 200 matching lines, 50 per file. Narrow path or glob for large repositories.",
            ("path", "Relative directory; '.' for the workspace root"), ("query", "Search text, or a regular expression when regex is true"),
            ("regex?:boolean", "Treat query as a regular expression (optional, default false)"), ("glob?", "File pattern such as *.cs or src/**/*.xaml (optional)"),
            ("whole_word?:boolean", "Match whole words only (optional)"), ("context?:integer", "Lines of context before and after each match, 0-3 (optional, default 0)"));
        Add("find_files", "Find files by name or path pattern. A pattern without / matches file names at any depth (e.g. *.csproj, *Service.cs); with / it matches paths relative to path (e.g. src/**/*.xaml). " +
            "Returns at most 500 relative paths. Read-only.",
            ("path", "Relative directory to search; '.' for the workspace root"), ("pattern", "Glob pattern: * and ? stay within one folder, ** matches any depth"));
        Add("read_file", "Read a text source file (UTF-8, UTF-16 or the system code page such as GBK) and obtain its SHA-256 version, encoding and line count. Must read existing files before writing. " +
            "Without start_line the whole file is returned (at most 2000 lines and the size limit, 1 MiB by default). For larger files pass start_line (1-based) and line_count (default 400, at most 5000) to read part of it; the SHA-256 always covers the whole file. " +
            "content shows each line as its line number, a tab, then the line text; the prefixes are not part of the file, so never copy them into old_text, new_text or content.",
            ("path", "Relative file path"), ("start_line?:integer", "First line to return, 1-based (optional)"), ("line_count?:integer", "Number of lines to return (optional)"));
        Add("write_file", "Create or replace a text file. Existing files keep their encoding; new files are UTF-8. Requires write permission. Existing files require the latest SHA-256 from read_file. Use 'new' only for a new file. Changes are written to disk and backed up.",
            ("path", "Relative file path"), ("content", "Complete new file content"), ("expected_sha256", "SHA-256 returned by read_file, or new"));
        Add("edit_file", "Replace exactly one occurrence of old_text in an existing file (or every occurrence with replace_all). Read first; preserve surrounding code. " +
            "Whole-line matches that differ only in trailing whitespace are accepted. Writes unify line endings to the file's dominant style. Fails on zero or multiple matches; the error says where the first line appears. " +
            "The result includes the updated region with line numbers and the added/removed line counts. " +
            "If the file changed since you read it, nothing is written and the error shows what changed; redo the edit against that.",
            ("path", "Relative file path"), ("old_text", "Exact nonempty text occurring once"), ("new_text", "Replacement text"),
            ("replace_all?:boolean", "Replace every occurrence instead of exactly one (optional)"),
            ("expected_sha256?", "SHA-256 from the latest read_file or write result (optional; defaults to the version you last read or wrote in this run)"));
        Add("replace_symbol", "Replace the whole declaration of a C#/VB type or member (including its doc comment and attributes) with new_text; empty new_text deletes it. " +
            "Located with the compiler, so no exact old_text is needed; ideal for 'replace member X' changes. The name must match exactly one declaration: qualify it with the type " +
            "and parameter types to pick an overload, e.g. OrderService.Save(int). new_text is the complete new declaration with indentation. Same approval, version check and backup as edit_file.",
            ("name", "Type or member name, e.g. OrderService.Save or OrderService.Save(int)"), ("new_text", "Complete new declaration, or empty to delete it"));
        Add("insert_near_symbol", "Insert new code (for example a new member) as whole lines directly before or after the declaration of a C#/VB type or member, located with the compiler. " +
            "The name must match exactly one declaration. text must include its indentation; add blank lines yourself if you want them.",
            ("name", "Existing type or member to anchor on, e.g. OrderService.Save"), ("text", "Code to insert"),
            ("position?", "before or after the declaration (optional, default after)"));
        Add("replace_between", "Replace a block of whole lines in an existing file: from the line matching start_line through the first line at or after it matching end_line " +
            "(both inclusive; lines match when equal after trimming whitespace). start_line must occur once in the file. Use it for 'replace fragment' changes given as start and end lines, " +
            "without copying the whole old block. Empty new_text deletes the lines. Same version check, approval and backup as edit_file.",
            ("path", "Relative file path"), ("start_line", "First line of the block, copied from the file"), ("end_line", "Last line of the block, copied from the file"),
            ("new_text", "Replacement lines, with indentation"),
            ("within?", "Type.Member whose lines limit the search, when the lines are unique only inside that member (optional)"),
            ("expected_sha256?", "SHA-256 from the latest read_file or write result (optional; defaults to the version you last read or wrote in this run)"));
        Add("multi_edit", "Apply several replacements to one file in order, as a single write (one backup). Each edit works like edit_file on the result of the previous edits; " +
            "if any edit fails, nothing is written. Prefer it over repeated edit_file calls on the same file.",
            ("path", "Relative file path"), ("edits:edits", "Edits in order, each {old_text, new_text, replace_all?}"),
            ("expected_sha256?", "SHA-256 from the latest read_file or write result (optional; defaults to the version you last read or wrote in this run)"));
        Add("delete_file", "Delete an existing text file. Read it first and pass its latest SHA-256. Agent mode only; the user may need to approve it; the file is backed up and restored by undo. " +
            "Update project files and code that reference the file.",
            ("path", "Relative file path"), ("expected_sha256", "Latest SHA-256 from read_file"));
        Add("move_file", "Move or rename a text file inside the workspace; new_path must not exist. Read the file first and pass its latest SHA-256. Agent mode only; the user may need to approve it. " +
            "Content is unchanged and backed up; update namespaces, project files and references yourself.",
            ("path", "Relative path of the existing file"), ("new_path", "Relative destination path"), ("expected_sha256", "Latest SHA-256 from read_file"));
        Add("run_tests", "Run tests with 'dotnet test' (SDK-style projects) and return counts plus failed tests with messages and stack traces. It builds the project first. " +
            "Agent mode only; 'dotnet test' must be on the user's command allowlist and the user may need to approve each run. Do not claim tests passed unless this tool reports succeeded=true.",
            ("project?", "Relative path of a test project, solution or directory (optional; the workspace root must then contain exactly one project or solution)"),
            ("filter?", "dotnet test --filter expression, e.g. FullyQualifiedName~OrderServiceTests (optional)"),
            ("timeout_seconds?:integer", "Timeout in seconds, default 600, at most 1800 (optional)"));
        Add("run_command", "Run one program from the user's command allowlist without a shell: no pipes, redirection, wildcards or variable expansion. command is the program name without a path (e.g. dotnet, git); " +
            "each argument is a separate array element and the first argument must match an allowed subcommand. Agent mode only; the user may need to approve each command. " +
            "Returns the exit code and the beginning and end of stdout and stderr. On timeout or stop the whole process tree is terminated.",
            ("command", "Program name, e.g. dotnet"), ("args?:array", "Arguments, one per element, e.g. [\"build\", \"src/App/App.csproj\"] (optional)"),
            ("cwd?", "Relative working directory (optional, default the workspace root)"),
            ("timeout_seconds?:integer", "Timeout in seconds, default 300, at most 1800 (optional)"));
        Add("build_solution", "Build the open solution in Visual Studio, or only one project and the projects it references, and wait for the result. Agent mode only; builds run project build events and may need the user's approval. " +
            "Returns success and up to 100 errors and warnings from the Error List. Use it to verify your changes, then fix reported errors; build a single project when only it changed.",
            ("project?", "Relative path of a project file to build instead of the whole solution (optional)"));
        Add("find_symbol", "Find C#/VB type and member declarations in the loaded solution by name (simple or qualified, e.g. OrderService or OrderService.Save). Returns kind, file, line and signature, at most 50. Read-only.",
            ("name", "Symbol name"));
        Add("find_references", "Find all references to C#/VB symbols with the given name (simple or qualified) using the compiler's semantic model, at most 300 locations sorted by file, each with the containing member and the line text. Read-only.",
            ("name", "Symbol name"));
        Add("get_outline", "List the types and members declared in a C#/VB file with line numbers, without reading the whole file. Read-only.",
            ("path", "Relative file path"));
        Add("get_hierarchy", "Show inheritance relationships of C#/VB symbols with the given name (simple or qualified) using the compiler's semantic model. " +
            "For a type: base classes, implemented interfaces, derived classes and implementing types; for a method, property or event: what it overrides or implements, " +
            "and what overrides or implements it. kind is the relationship and text is the matched symbol; framework symbols have no file. At most 200 results. Read-only.",
            ("name", "Type or member name, e.g. IQueueService or ShapeBase.Area"));
        Add("check_file", "Get the compiler errors and warnings of one C#/VB file as it is on disk, using Visual Studio's loaded solution, without building. " +
            "Much faster than build_solution; use it after edits, and build_solution for a final check across projects. Writes to .cs/.vb files already include the file's compile_errors. Read-only.",
            ("path", "Relative file path"));
        Add("read_symbol", "Return the source code of C#/VB declarations with the given name (simple or qualified), including their doc comments, with file and line range; " +
            "at most 3 matches and 400 lines each. Faster than reading whole files. You can then edit_file / multi_edit those files without a separate read_file. Read-only.",
            ("name", "Type or member name, e.g. OrderService or OrderService.Save"));
        Add("get_callers", "Find the members that call the C#/VB method, property or constructor with the given name: caller, file, line and the calling line. Read-only.",
            ("name", "Member name, e.g. OrderService.Save"));
        Add("get_callees", "List what a C#/VB method, property or event uses in its body: methods it calls, types it creates, and properties/fields/events it uses, " +
            "each with its declaration location (framework members have no file) and the first calling line. Read-only.",
            ("name", "Member name, e.g. OrderService.Save"));
        Add("get_type_dependencies", "List the types a C#/VB type depends on: base types and interfaces (inherits), types in member signatures (signature), " +
            "and types created or called in member bodies (body), excluding primitives. Combine with find_references on the type for its dependents. Read-only.",
            ("name", "Type name, e.g. OrderService"));
        Add("get_project_graph", "List the projects loaded in the Visual Studio solution with their language, project file, document count and the projects each one references. Read-only.");
        Add("update_plan", "Record or update your step-by-step plan so the user can follow progress. Use it for tasks with three or more steps: once at the start and again whenever a step's status changes. " +
            "One step per line, each starting with done:, doing: or todo:; at most one step is doing. It changes nothing in the workspace.",
            ("plan", "Lines such as 'done: Read OrderService.cs', 'doing: Add parameter validation', 'todo: Build and fix errors'"));
        Add("get_errors", "Read the current errors and warnings in the Visual Studio Error List (build and IntelliSense), errors first, at most 100. Read-only; does not build.",
            ("path?", "Only diagnostics in this relative file (optional)"), ("errors_only?:boolean", "Only errors, no warnings (optional)"));
        foreach (var tool in external ?? [])
        {
            var schema = (JsonObject)tool.InputSchema.DeepClone();
            if (claude) tools.Add(new JsonObject { ["name"] = tool.ExposedName, ["description"] = tool.Description, ["input_schema"] = schema });
            else tools.Add(new JsonObject { ["type"] = "function", ["function"] = new JsonObject
                { ["name"] = tool.ExposedName, ["description"] = tool.Description, ["parameters"] = schema } });
        }
        return tools;

        void Add(string name, string description, params (string Name, string Description)[] fields)
        {
            var properties = new JsonObject();
            var required = new JsonArray();
            // 字段名形如 "name?:integer"：? 表示可选，冒号后为 JSON 类型（默认 string）。
            foreach (var field in fields)
            {
                var spec = field.Name.Split(':');
                var optional = spec[0].EndsWith("?", StringComparison.Ordinal);
                var fieldName = spec[0].TrimEnd('?');
                var type = spec.Length > 1 ? spec[1] : "string";
                properties[fieldName] = type switch
                {
                    "array" => new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = field.Description },
                    "edits" => new JsonObject
                    {
                        ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 50, ["description"] = field.Description,
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("old_text", "new_text"),
                            ["properties"] = new JsonObject
                            {
                                ["old_text"] = new JsonObject { ["type"] = "string", ["description"] = "Exact nonempty text occurring once (unless replace_all)" },
                                ["new_text"] = new JsonObject { ["type"] = "string", ["description"] = "Replacement text" },
                                ["replace_all"] = new JsonObject { ["type"] = "boolean", ["description"] = "Replace every occurrence (optional)" },
                            },
                        },
                    },
                    _ => new JsonObject { ["type"] = type, ["description"] = field.Description },
                };
                if (!optional) required.Add(fieldName);
            }
            var schema = new JsonObject { ["type"] = "object", ["properties"] = properties,
                ["required"] = required, ["additionalProperties"] = false };
            if (claude) tools.Add(new JsonObject { ["name"] = name, ["description"] = description, ["input_schema"] = schema });
            else tools.Add(new JsonObject { ["type"] = "function", ["function"] = new JsonObject
                { ["name"] = name, ["description"] = description, ["parameters"] = schema } });
        }
    }
}

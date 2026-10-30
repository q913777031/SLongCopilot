using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SLCopilot.Core;

/// <param name="onRetry">暂时性失败重试前的回调（第几次重试、等待时间），可在任意线程调用。</param>
/// <param name="maxAttempts">最多尝试次数，见 <see cref="ProviderRetry"/>。</param>
/// <param name="onUsage">流结束时回调服务商返回的 token 用量；未返回用量时不回调。</param>
public sealed class AiClient(HttpClient http, Action<int, TimeSpan>? onRetry = null, int maxAttempts = ProviderRetry.MaxAttempts,
    Action<TokenUsage>? onUsage = null)
{
    public async IAsyncEnumerable<string> StreamAsync(ProviderSettings settings, string apiKey,
        string systemPrompt, IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var uri = settings.RequestUri();
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("API key is missing. Set the configured environment variable and restart Visual Studio.");
        if (messages.Count == 0 || messages.Any(m => m.Role is not ("user" or "assistant")))
            throw new ArgumentException("Conversation must contain user/assistant messages.");
        var payload = new JsonObject { ["model"] = settings.Model, ["stream"] = true };
        if (settings.Provider == ProviderKind.Claude)
        {
            payload["system"] = systemPrompt;
            ClaudeRequest.Apply(payload, settings);
            payload["messages"] = Messages(messages, claude: true);
        }
        else
        {
            OpenAiRequest.Apply(payload, settings);
            payload["messages"] = Messages([new ChatMessage("system", systemPrompt), .. messages], claude: false);
        }
        var body = payload.ToJsonString();
        using var response = await ProviderRetry.SendAsync(http, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (settings.Provider == ProviderKind.Claude) ClaudeRequest.AddHeaders(request, settings, apiKey);
            else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            return request;
        }, onRetry, cancellationToken, maxAttempts);
        // net472 的响应流读取不响应取消令牌：取消时释放响应以中断读取，SseReader 负责转换为取消异常。
        using var abort = cancellationToken.Register(response.Dispose);
        using var stream = await Compat.ReadAsStreamAsync(response.Content, cancellationToken);
        using var reader = new StreamReader(stream);
        var received = 0;
        string? stopReason = null;
        var usage = new UsageCounter();
        await foreach (var data in SseReader.ReadAsync(reader, cancellationToken))
        {
            if (data == "[DONE]")
            {
                if (usage.Result is { } total) onUsage?.Invoke(total);
                yield break;
            }
            using var json = JsonDocument.Parse(data);
            var root = json.RootElement;
            if (root.TryGetProperty("error", out _)) throw new IOException("Provider reported a streaming error. Retry the request.");
            string? text = null;
            if (settings.Provider == ProviderKind.Claude)
            {
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "message_start" && root.TryGetProperty("message", out var started) && started.TryGetProperty("usage", out var startUsage))
                    usage.ReadClaude(JsonNode.Parse(startUsage.GetRawText()));
                if (type == "message_delta" && root.TryGetProperty("usage", out var deltaUsage))
                    usage.ReadClaude(JsonNode.Parse(deltaUsage.GetRawText()));
                if (type == "message_stop")
                {
                    if (usage.Result is { } total) onUsage?.Invoke(total);
                    if (stopReason == "max_tokens") throw new IOException("Output was truncated by the provider token limit.");
                    if (stopReason == "refusal") throw new IOException("The model declined this request. Partial output was discarded.");
                    yield break;
                }
                if (type == "message_delta" && root.TryGetProperty("delta", out var metadata) &&
                    metadata.TryGetProperty("stop_reason", out var reason)) stopReason = reason.GetString();
                if (type == "content_block_delta" && root.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("text", out var content)) text = content.GetString();
            }
            else if (root.TryGetProperty("usage", out var openAiUsage) && openAiUsage.ValueKind == JsonValueKind.Object)
                usage.ReadOpenAi(JsonNode.Parse(openAiUsage.GetRawText()));
            if (settings.Provider != ProviderKind.Claude && root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                if (choices[0].TryGetProperty("finish_reason", out var reason) &&
                    reason.GetString() is "length" or "content_filter")
                    throw new IOException("Output was truncated or filtered by the provider.");
                if (choices[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String) text = content.GetString();
            }
            if (text is { Length: > 0 })
            {
                received += text.Length;
                if (received > 200_000) throw new IOException("Response exceeded the 200,000 character safety limit.");
                yield return text;
            }
        }
        throw new IOException("Provider stream ended before its completion marker. Partial output was not committed to conversation history.");
    }

    private static JsonArray Messages(IEnumerable<ChatMessage> messages, bool claude)
        => new(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = ImageContent.UserContent(claude, m.Content, m.Role == "user" ? m.Images : null) }).ToArray());
}

using System.Text.Json.Nodes;

namespace SLCopilot.Core;

/// <summary>
/// 一次请求的 token 用量。<see cref="InputTokens"/> 不含缓存部分（OpenAI 的 prompt_tokens 已扣除 cached_tokens），
/// 因此 <see cref="ContextTokens"/> 是本次请求实际送入模型的上下文大小。服务商未返回用量时为 null，不做估算。
/// </summary>
public sealed record TokenUsage(long InputTokens, long OutputTokens, long CacheReadTokens = 0, long CacheWriteTokens = 0)
{
    public long ContextTokens => InputTokens + CacheReadTokens + CacheWriteTokens;

    public static TokenUsage operator +(TokenUsage a, TokenUsage b) => new(
        a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens, a.CacheReadTokens + b.CacheReadTokens, a.CacheWriteTokens + b.CacheWriteTokens);
}

/// <summary>从流事件中累积用量；字段出现多次时以最后一次为准（Claude 的 message_delta 给出累计值）。</summary>
internal sealed class UsageCounter
{
    private long? input, output, cacheRead, cacheWrite;

    public void ReadClaude(JsonNode? usage)
    {
        if (usage is not JsonObject u) return;
        input = Long(u["input_tokens"]) ?? input;
        output = Long(u["output_tokens"]) ?? output;
        cacheRead = Long(u["cache_read_input_tokens"]) ?? cacheRead;
        cacheWrite = Long(u["cache_creation_input_tokens"]) ?? cacheWrite;
    }

    public void ReadOpenAi(JsonNode? usage)
    {
        if (usage is not JsonObject u) return;
        var cached = Long(u["prompt_tokens_details"]?["cached_tokens"]) ?? 0;
        if (Long(u["prompt_tokens"]) is { } prompt) { input = Math.Max(0, prompt - cached); cacheRead = cached; }
        output = Long(u["completion_tokens"]) ?? output;
    }

    /// <summary>OpenAI Responses API 的 usage：input_tokens 含缓存命中（input_tokens_details.cached_tokens），拆成未命中与命中两部分。</summary>
    public void ReadResponses(JsonNode? usage)
    {
        if (usage is not JsonObject u) return;
        var cached = Long(u["input_tokens_details"]?["cached_tokens"]) ?? 0;
        if (Long(u["input_tokens"]) is { } total) { input = Math.Max(0, total - cached); cacheRead = cached; }
        output = Long(u["output_tokens"]) ?? output;
    }

    /// <summary>服务端响应中的模型 ID（Claude 为 message_start.message.model，OpenAI 兼容服务为 chunk.model）；用于确认实际使用的模型。</summary>
    public string? ServedModel { get; set; }

    public TokenUsage? Result => input is null && output is null ? null : new(input ?? 0, output ?? 0, cacheRead ?? 0, cacheWrite ?? 0);

    private static long? Long(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var result) ? result : null;
}

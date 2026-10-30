using System.Net.Http;
using System.Text.Json.Nodes;

namespace SLCopilot.Core;

/// <summary>Claude 请求的公共参数：adaptive thinking、effort，以及仅对官方端点启用的 prompt caching 与服务端拒答回退。</summary>
internal static class ClaudeRequest
{
    private const string FallbackBeta = "server-side-fallback-2026-07-01";
    private const string ProgressBeta = "thinking-display-updates-2026-08-18";
    /// <summary>思考记录绑定的控制参数（官方端点）：对话前缀变化时只丢弃失效的 thinking 块，而不是返回 400。</summary>
    public const string BindingBeta = "thinking-binding-controls-2026-08-01";
    /// <summary>按需服务端压缩；请求摘要时以及此后每个携带 compaction 块的请求都要带。</summary>
    public const string CompactionBeta = "compact-2026-09-04";
    /// <summary>有“思考记录与对话绑定”校验的模型（Claude 官方迁移文档）；只对它们发送 block_binding。</summary>
    private static readonly string[] BindingModels = ["claude-opus-5-5", "claude-fable-5-1", "claude-sonnet-5-5", "claude-haiku-5-5"];
    /// <summary>支持按需服务端压缩的模型（官方文档 compaction-on-demand 的 supportedModels）。</summary>
    private static readonly string[] CompactionModels =
    [
        "claude-fable-5-1", "claude-fable-5", "claude-opus-5-5", "claude-opus-5", "claude-sonnet-5-5", "claude-sonnet-5", "claude-haiku-5-5",
        "claude-opus-4-8", "claude-opus-4-7", "claude-opus-4-6", "claude-sonnet-4-6",
    ];
    private static readonly string[] FallbackModels = ["claude-opus-5-5", "claude-opus-5", "claude-fable-5-1", "claude-sonnet-5-5"];
    private static readonly string[] ProgressModels = ["claude-opus-5-5", "claude-fable-5-1", "claude-fable-5", "claude-sonnet-5-5"];

    /// <param name="progressUpdates">为 true 时在官方端点上请求 thinking.display = updates：工具调用之间的进度说明以 thinking 块返回。</param>
    /// <param name="fallbacks">为 false 时不发送服务端拒答回退（压缩请求不使用）。</param>
    public static void Apply(JsonObject payload, ProviderSettings settings, bool progressUpdates = false, bool fallbacks = true)
    {
        payload["max_tokens"] = settings.OutputTokenLimit();
        if (!string.IsNullOrEmpty(settings.Effort))
        {
            var thinking = new JsonObject { ["type"] = "adaptive" };
            if (progressUpdates && UsesProgressUpdates(settings)) thinking["display"] = "updates";
            // 历史被改动过（旧版本保存的对话、system 或工具列表变化）时，失效的 thinking 块被丢弃而不是让整个请求失败。
            if (UsesBinding(settings)) thinking["block_binding"] = new JsonObject { ["prefix_mismatch_behavior"] = "drop_block" };
            payload["thinking"] = thinking;
            payload["output_config"] = new JsonObject { ["effort"] = settings.Effort };
        }
        if (settings.IsAnthropicFirstParty) payload["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
        if (fallbacks && UsesFallback(settings)) payload["fallbacks"] = "default";
    }

    /// <param name="extraBetas">额外的 beta 名称（如携带 compaction 块时的 <see cref="CompactionBeta"/>）。</param>
    public static void AddHeaders(HttpRequestMessage request, ProviderSettings settings, string key, bool progressUpdates = false,
        bool fallbacks = true, IEnumerable<string>? extraBetas = null)
    {
        request.Headers.Add("x-api-key", key);
        request.Headers.Add("anthropic-version", "2023-06-01");
        var betas = new List<string>();
        if (fallbacks && UsesFallback(settings)) betas.Add(FallbackBeta);
        if (progressUpdates && UsesProgressUpdates(settings)) betas.Add(ProgressBeta);
        if (UsesBinding(settings)) betas.Add(BindingBeta);
        if (extraBetas is not null) betas.AddRange(extraBetas.Where(b => !betas.Contains(b)));
        if (betas.Count > 0) request.Headers.Add("anthropic-beta", string.Join(",", betas));
    }

    /// <summary>是否使用按需服务端压缩：只在官方端点与支持的模型上。</summary>
    public static bool SupportsServerCompaction(ProviderSettings settings)
        => settings.IsAnthropicFirstParty && CompactionModels.Contains(settings.Model);

    /// <summary>历史中是否有服务端压缩块（此后每个请求都要带 <see cref="CompactionBeta"/>）。</summary>
    public static bool HasCompactionBlock(JsonArray messages)
        => messages.OfType<JsonObject>().Any(m => m["content"] is JsonArray blocks && blocks.OfType<JsonObject>().Any(b => (string?)b["type"] == "compaction"));

    /// <summary>
    /// 服务端切换到备用模型时，最后一个 fallback 块之前的非文本块（thinking、tool_use 等）不得回传，fallback 标记块本身也去掉。
    /// 返回的数组即为可原样写回历史的 assistant content。
    /// </summary>
    public static JsonArray RemoveFallbackBoundary(JsonArray blocks)
    {
        var last = -1;
        for (var i = 0; i < blocks.Count; i++)
            if ((string?)blocks[i]?["type"] == "fallback") last = i;
        if (last < 0) return (JsonArray)blocks.DeepClone();
        var result = new JsonArray();
        for (var i = 0; i < blocks.Count; i++)
        {
            var type = (string?)blocks[i]?["type"];
            if (type == "fallback" || (i < last && type != "text")) continue;
            result.Add(blocks[i]!.DeepClone());
        }
        return result;
    }

    private static bool UsesFallback(ProviderSettings settings)
        => settings.IsAnthropicFirstParty && FallbackModels.Contains(settings.Model);

    /// <summary>请求是否带 drop_block：带时服务端自行丢弃失效的 thinking 块，400 不会由思考记录引起。</summary>
    public static bool DropsInvalidThinking(ProviderSettings settings) => UsesBinding(settings);

    private static bool UsesBinding(ProviderSettings settings)
        => settings.IsAnthropicFirstParty && !string.IsNullOrEmpty(settings.Effort) && BindingModels.Contains(settings.Model);

    private static bool UsesProgressUpdates(ProviderSettings settings)
        => settings.IsAnthropicFirstParty && !string.IsNullOrEmpty(settings.Effort) && ProgressModels.Contains(settings.Model);
}

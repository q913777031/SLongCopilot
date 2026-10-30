using System.Net.Http.Headers;
using System.Text.Json;

namespace SLCopilot.Core;

/// <summary>服务商返回的模型信息。上下文窗口与输出上限只有 Claude 的 /v1/models 返回（max_input_tokens、max_tokens），其他服务为 null。</summary>
public sealed record ModelInfo(string Id, int? MaxInputTokens, int? MaxOutputTokens);

/// <summary>模型选择器使用的显示名与推荐列表。</summary>
public static class ModelNames
{
    /// <summary>官方 Claude 端点上默认列出的模型（与 Claude Code 的模型菜单一致）；其他模型在“更多模型”中。</summary>
    public static readonly IReadOnlyList<string> DeepSeekFeatured = ["deepseek-flash", "deepseek-v4-pro"];
    public static readonly IReadOnlyList<string> ClaudeFeatured = ["claude-opus-5-5", "claude-fable-5-1", "claude-sonnet-5-5", "claude-haiku-5-5"];
    /// <summary>官方 OpenAI 端点上默认列出的模型（OpenAI 模型目录，2026-10）：Astra 旗舰，6.1 Sol 性价比，6.1 Terra，Luna 低成本。</summary>
    public static readonly IReadOnlyList<string> OpenAiFeatured = ["gpt-6-astra", "gpt-6.1-sol", "gpt-6.1-terra", "gpt-6-luna"];
    /// <summary>GPT-6 系列（含快照 ID，如 gpt-6-astra-2026-09-03）：在 Chat Completions 中不能（或只能在不思考时）调用工具，智能体走 Responses API。</summary>
    private static readonly string[] Gpt6Models = ["gpt-6-astra", "gpt-6.1-sol", "gpt-6.1-terra", "gpt-6-luna", "gpt-6-sol"];
    /// <summary>不支持 reasoning effort none 的 GPT-6 模型（OpenAI《Using GPT-6》）。</summary>
    private static readonly string[] Gpt6WithoutNoneEffort = ["gpt-6-astra", "gpt-6.1-sol"];

    public static bool IsGpt6(string model) => Matches(model, Gpt6Models);

    /// <summary>OpenAI 模型可选的 reasoning_effort（空表示不发送，由模型决定）。GPT-6 系列支持到 xhigh / max，Astra 与 6.1 Sol 不支持 none；其他模型保持低 / 中 / 高。</summary>
    public static IReadOnlyList<string> OpenAiEffortLevels(string model) =>
        !IsGpt6(model) ? ProviderSettings.ReasoningEffortLevels
        : Matches(model, Gpt6WithoutNoneEffort) ? ["", "low", "medium", "high", "xhigh", "max"]
        : ["", "none", "low", "medium", "high", "xhigh", "max"];

    private static bool Matches(string model, IEnumerable<string> ids)
    {
        var id = (model ?? "").Trim().ToLowerInvariant();
        return ids.Any(p => id == p || id.StartsWith(p + "-", StringComparison.Ordinal));
    }

    /// <summary>把 claude-opus-5-5 显示为 Opus 5.5、gpt-6.1-sol 显示为 GPT-6.1 Sol；其他 ID（含带日期的快照）原样显示。</summary>
    public static string Label(string model)
    {
        var id = (model ?? "").Trim();
        var parts = id.Split('-');
        if (IsGpt6(id) && parts.Length >= 2 && parts[1].Length > 0 && parts[1].All(c => char.IsDigit(c) || c == '.') && char.IsDigit(parts[1][0])
            && parts.Skip(2).All(p => p.Length > 0 && p.All(char.IsLetter)))
            return "GPT-" + parts[1] + string.Concat(parts.Skip(2).Select(p => " " + char.ToUpperInvariant(p[0]) + p.Substring(1)));
        if (parts.Length >= 3 && parts[0] == "claude" && parts.Skip(2).All(p => p.All(char.IsDigit)) && parts[2].Length <= 2)
        {
            var family = char.ToUpperInvariant(parts[1][0]) + parts[1].Substring(1);
            var version = string.Join(".", parts.Skip(2).TakeWhile(p => p.Length <= 2));
            return family + " " + version;
        }
        return id;
    }

    /// <summary>思考强度的中文显示名；空值为“默认”（不发送强度，由模型决定）。</summary>
    public static string EffortLabel(string effort) => (effort ?? "") switch
    {
        "" => "默认",
        "none" => "关闭思考",
        "low" => "低",
        "medium" => "中",
        "high" => "高",
        "xhigh" => "超高",
        "max" => "最高",
        var other => other,
    };
}

/// <summary>
/// 已知模型的上下文窗口（Claude 官方模型表，2026-10-06）。用于在没有联网获取时迁移旧默认值；以服务商实时返回的值为准。
/// </summary>
public static class ModelLimits
{
    public const int LegacyContextWindow = 200_000, LegacyMaxOutputTokens = 16_000;
    private static readonly string[] MillionTokenPrefixes =
    [
        "claude-fable-5", "claude-mythos-5", "claude-opus-5", "claude-sonnet-5", "claude-haiku-5",
        "claude-opus-4-8", "claude-opus-4-7", "claude-opus-4-6", "claude-sonnet-4-6",
    ];

    /// <summary>已知支持 128K 输出的 Claude 模型（与 1M 上下文的模型相同）的默认输出上限 64,000；其他模型不给建议值。</summary>
    public const int LongOutputTokens = 64_000;

    /// <summary>GPT-6 系列的上下文窗口（OpenAI 模型目录：1.05M）。</summary>
    public const int Gpt6ContextWindow = 1_050_000;

    public static int? KnownMaxOutputTokens(ProviderKind provider, string model) =>
        provider == ProviderKind.Claude && KnownContextWindow(model) == 1_000_000 ? LongOutputTokens
        : provider == ProviderKind.OpenAI && ModelNames.IsGpt6(model) ? LongOutputTokens : null;

    public static int? KnownContextWindow(string model)
    {
        var id = (model ?? "").Trim().ToLowerInvariant();
        if (MillionTokenPrefixes.Any(p => id == p || id.StartsWith(p + "-", StringComparison.Ordinal))) return 1_000_000;
        if (ModelNames.IsGpt6(id)) return Gpt6ContextWindow;
        if (id.StartsWith("claude-haiku-4-5", StringComparison.Ordinal)) return 200_000;
        return null;
    }
}

/// <summary>
/// 从服务商的 <c>GET {base}/models</c> 读取可用模型 ID。Claude 与 OpenAI 兼容服务都返回 <c>data[].id</c>。
/// 失败时抛出 <see cref="ProviderHttpException"/>（非成功状态码）或 IOException（响应格式不符）；不重试。
/// </summary>
public static class ModelCatalog
{
    private const int MaxResponseBytes = 4_000_000;

    public static async Task<IReadOnlyList<string>> ListAsync(HttpClient http, ProviderSettings settings, string apiKey, CancellationToken cancellationToken)
        => (await ListDetailedAsync(http, settings, apiKey, cancellationToken).ConfigureAwait(false)).Select(m => m.Id).ToList();

    /// <summary>与 <see cref="ListAsync"/> 相同，同时返回每个模型的上下文窗口与输出上限（服务商提供时）。</summary>
    public static async Task<IReadOnlyList<ModelInfo>> ListDetailedAsync(HttpClient http, ProviderSettings settings, string apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("API key is missing.");
        settings.RequestUri();
        var claude = settings.Provider == ProviderKind.Claude;
        var baseUri = new Uri(settings.Endpoint.TrimEnd('/') + "/", UriKind.Absolute);
        var uri = new Uri(baseUri, claude ? "models?limit=1000" : "models");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (claude)
        {
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
        }
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new ProviderHttpException(response.StatusCode, $"Provider returned HTTP {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength is > MaxResponseBytes) throw new IOException("Model list response is too large.");
        using var stream = await Compat.ReadAsStreamAsync(response.Content, cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaxResponseBytes) throw new IOException("Model list response is too large.");
            buffer.Write(bytes, 0, count);
        }
        using var json = JsonDocument.Parse(buffer.ToArray());
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new IOException("Model list response has no data array.");
        var models = new SortedDictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in data.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                id.GetString() is { Length: > 0 and <= 200 } value && !models.ContainsKey(value))
                models[value] = new(value, Int(item, "max_input_tokens") ?? Int(item, "context_window"), Int(item, "max_tokens") ?? Int(item, "max_output_tokens"));
        if (models.Count == 0) throw new IOException("The provider returned no models.");
        return models.Values.ToList();

        static int? Int(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0 ? number : null;
    }
}

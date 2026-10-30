namespace SLCopilot.Core;

public enum ProviderKind { OpenAI, Claude, DeepSeek }

public sealed record ProviderSettings
{
    public const int OpenAiMaxOutputTokens = 32768;
    /// <summary>GPT-6 系列的输出上限（OpenAI 模型目录：128K，含推理 tokens）。</summary>
    public const int Gpt6MaxOutputTokens = 128_000;
    public static readonly string[] EffortLevels = ["", "low", "medium", "high", "xhigh", "max"];
    /// <summary>OpenAI 推理模型的 reasoning_effort；空表示不发送（普通模型收到该参数会返回 400）。</summary>
    public static readonly string[] ReasoningEffortLevels = ["", "low", "medium", "high"];

    public static readonly string[] DeepSeekEffortLevels = ["", "none", "low", "high", "max"];

    public bool IsDeepSeek => Provider == ProviderKind.DeepSeek || (Provider == ProviderKind.OpenAI &&
        Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) && uri.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase));

    public ProviderKind Provider { get; init; }
    public string Endpoint { get; init; } = "https://api.openai.com/v1";
    public string Model { get; init; } = "gpt-4.1";
    /// <summary>读取 API 密钥的环境变量名；为空表示只使用宿主保存的密钥（如 Windows 凭据管理器）。</summary>
    public string KeyEnvironmentVariable { get; init; } = "OPENAI_API_KEY";
    /// <summary>
    /// 单次回答的输出上限（tokens）。默认 16,000（各类服务普遍可用）；已知支持 128K 输出的 Claude 模型配置为 64,000（见 <see cref="ModelLimits"/>）。
    /// OpenAI 兼容服务按 32,768 截断。
    /// </summary>
    public int MaxOutputTokens { get; init; } = ModelLimits.LegacyMaxOutputTokens;
    /// <summary>Claude 的 output_config.effort；为空时不发送 thinking 与 effort（用于不支持 adaptive thinking 的旧模型）。</summary>
    public string Effort { get; init; } = "high";
    /// <summary>行内补全使用的模型 ID；为空时使用 <see cref="Model"/>。建议填写快速、低成本的模型。</summary>
    public string CompletionModel { get; init; } = "";
    /// <summary>OpenAI 兼容服务的推理强度（reasoning_effort）。与 Claude 的 <see cref="Effort"/> 分开保存：默认不发送，只有用户为推理模型选择后才发送。</summary>
    public string OpenAiReasoningEffort { get; init; } = "";
    /// <summary>模型上下文窗口（tokens），用于自动压缩：最近一次请求超过它的 80% 时总结较早的对话。0 表示关闭自动压缩。按模型文档填写。</summary>
    public int ContextWindow { get; init; } = 200_000;

    /// <summary>端点是否为 Anthropic 官方 API；仅此时才发送第三方网关可能拒绝的 beta 头与字段。</summary>
    public bool IsAnthropicFirstParty => Provider == ProviderKind.Claude &&
        Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Host, "api.anthropic.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 智能体是否走 OpenAI Responses API：官方 OpenAI 端点上的 GPT-6 系列（在 Chat Completions 中不能调用工具）。
    /// 请求 store: false，并回传加密推理内容；普通聊天、摘要与行内补全仍用 Chat Completions。
    /// </summary>
    public bool UsesResponsesApi => IsOpenAiFirstParty && ModelNames.IsGpt6(Model);

    /// <summary>端点是否为 OpenAI 官方 API；仅此时才请求流式用量（stream_options），兼容网关可能拒绝该字段。</summary>
    public bool IsOpenAiFirstParty => Provider == ProviderKind.OpenAI &&
        Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase);

    public int OutputTokenLimit() => Provider == ProviderKind.Claude ? MaxOutputTokens
        : Math.Min(MaxOutputTokens, IsOpenAiFirstParty && ModelNames.IsGpt6(Model) ? Gpt6MaxOutputTokens : OpenAiMaxOutputTokens);

    public Uri RequestUri() => new(BaseUri(), Provider == ProviderKind.Claude ? "messages" : "chat/completions");

    /// <summary>OpenAI Responses API 的地址（{base}/responses）；校验同 <see cref="RequestUri"/>。</summary>
    public Uri ResponsesUri() => new(BaseUri(), "responses");

    private Uri BaseUri()
    {
        if (!Enum.IsDefined(typeof(ProviderKind), Provider)) throw new ArgumentException("Unknown provider.");
        if (!Uri.TryCreate(Endpoint.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Endpoint must be an HTTPS base URL without credentials, query or fragment.");
        if (string.IsNullOrWhiteSpace(Model)) throw new ArgumentException("Model is required.");
        if (KeyEnvironmentVariable is null || KeyEnvironmentVariable.Length > 128) throw new ArgumentException("Invalid API key environment variable name.");
        if (MaxOutputTokens is < 1 or > 128000) throw new ArgumentException("Invalid output token limit.");
        if (CompletionModel is null || CompletionModel.Length > 200) throw new ArgumentException("Invalid completion model.");
        if (ContextWindow != 0 && ContextWindow is < 1000 or > 100_000_000) throw new ArgumentException("Context window must be 0 (off) or between 1,000 and 100,000,000 tokens.");
        if (!(IsDeepSeek ? DeepSeekEffortLevels.Concat(new[] { "medium" }) : ModelNames.OpenAiEffortLevels(Model)).Contains(OpenAiReasoningEffort ?? "")) throw new ArgumentException("Invalid reasoning effort for this model.");
        if (!EffortLevels.Contains(Effort ?? "")) throw new ArgumentException("Effort must be empty, low, medium, high, xhigh or max.");
        return uri;
    }
}

public sealed record ChatMessage(string Role, string Content)
{
    /// <summary>随用户消息发送的图片；只在内存中保留，不写入保存的对话（重新打开后只剩文字）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<ImageAttachment>? Images { get; init; }
}

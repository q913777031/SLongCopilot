using System.Text.Json.Nodes;

namespace SLCopilot.Core;

internal static class OpenAiRequest
{
    public static void Apply(JsonObject payload, ProviderSettings settings)
    {
        payload[settings.IsDeepSeek ? "max_tokens" : "max_completion_tokens"] = settings.OutputTokenLimit();
        var effort = settings.IsDeepSeek && settings.OpenAiReasoningEffort == "medium" ? "high" : settings.OpenAiReasoningEffort;
        if (settings.IsDeepSeek && effort == "none")
            payload["thinking"] = new JsonObject { ["type"] = "disabled" };
        else if (!string.IsNullOrEmpty(effort))
        {
            payload["reasoning_effort"] = effort;
            if (settings.IsDeepSeek) payload["thinking"] = new JsonObject { ["type"] = "enabled" };
        }
        if (settings.IsOpenAiFirstParty)
            payload["stream_options"] = new JsonObject { ["include_usage"] = true };
    }
}

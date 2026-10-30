using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SLCopilot.Core.Tests;

/// <summary>把测试里按非流式格式写的响应转换成 SSE 事件流，文本与工具参数都拆成两段以覆盖拼接逻辑。</summary>
internal static class Sse
{
    public static string Convert(string nonStreamingJson)
    {
        var root = JsonNode.Parse(nonStreamingJson)!.AsObject();
        return root.ContainsKey("choices") ? OpenAi(root) : Claude(root);
    }

    private static string Claude(JsonObject root)
    {
        var sb = new StringBuilder();
        Event(sb, new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject() });
        var index = 0;
        foreach (var node in root["content"]?.AsArray() ?? new JsonArray())
        {
            var block = node!.AsObject();
            var type = (string?)block["type"] ?? "";
            var start = (JsonObject)block.DeepClone();
            switch (type)
            {
                case "text":
                    start["text"] = "";
                    Event(sb, Start(index, start));
                    foreach (var piece in Halves((string?)block["text"] ?? ""))
                        Event(sb, Delta(index, new JsonObject { ["type"] = "text_delta", ["text"] = piece }));
                    break;
                case "tool_use":
                    start["input"] = new JsonObject();
                    Event(sb, Start(index, start));
                    foreach (var piece in Halves(block["input"]?.ToJsonString() ?? "{}"))
                        Event(sb, Delta(index, new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = piece }));
                    break;
                case "thinking":
                    start["thinking"] = ""; start["signature"] = "";
                    Event(sb, Start(index, start));
                    Event(sb, Delta(index, new JsonObject { ["type"] = "thinking_delta", ["thinking"] = (string?)block["thinking"] ?? "" }));
                    Event(sb, Delta(index, new JsonObject { ["type"] = "signature_delta", ["signature"] = (string?)block["signature"] ?? "" }));
                    break;
                default:
                    Event(sb, Start(index, start));
                    break;
            }
            Event(sb, new JsonObject { ["type"] = "content_block_stop", ["index"] = index });
            index++;
        }
        if (root["stop_reason"] is JsonValue stop)
            Event(sb, new JsonObject { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = stop.DeepClone() } });
        Event(sb, new JsonObject { ["type"] = "message_stop" });
        return sb.ToString();
    }

    private static string OpenAi(JsonObject root)
    {
        var sb = new StringBuilder();
        var choice = root["choices"]![0]!.AsObject();
        var message = choice["message"]?.AsObject() ?? new JsonObject();
        if ((string?)message["content"] is { Length: > 0 } content)
            foreach (var piece in Halves(content)) Event(sb, Choice(new JsonObject { ["content"] = piece }));
        if ((string?)message["refusal"] is { Length: > 0 } refusal) Event(sb, Choice(new JsonObject { ["refusal"] = refusal }));
        var calls = message["tool_calls"]?.AsArray() ?? new JsonArray();
        for (var i = 0; i < calls.Count; i++)
        {
            var call = calls[i]!.AsObject();
            var arguments = Halves((string?)call["function"]?["arguments"] ?? "").ToArray();
            Event(sb, Choice(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["index"] = i, ["id"] = call["id"]?.DeepClone(), ["type"] = call["type"]?.DeepClone(),
                ["function"] = new JsonObject { ["name"] = call["function"]?["name"]?.DeepClone(), ["arguments"] = arguments.Length > 0 ? arguments[0] : "" },
            }) }));
            if (arguments.Length > 1)
                Event(sb, Choice(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject
                { ["index"] = i, ["function"] = new JsonObject { ["arguments"] = arguments[1] } }) }));
        }
        Event(sb, new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject(), ["finish_reason"] = choice["finish_reason"]?.DeepClone() }) });
        sb.Append("data: [DONE]\n\n");
        return sb.ToString();
    }

    private static JsonObject Start(int index, JsonObject block) => new() { ["type"] = "content_block_start", ["index"] = index, ["content_block"] = block };
    private static JsonObject Delta(int index, JsonObject delta) => new() { ["type"] = "content_block_delta", ["index"] = index, ["delta"] = delta };
    private static JsonObject Choice(JsonObject delta) => new() { ["choices"] = new JsonArray(new JsonObject { ["delta"] = delta }) };
    private static void Event(StringBuilder sb, JsonObject payload) => sb.Append("data: ").Append(payload.ToJsonString()).Append("\n\n");

    private static IEnumerable<string> Halves(string text)
    {
        if (text.Length < 2) { yield return text; yield break; }
        yield return text.Substring(0, text.Length / 2);
        yield return text.Substring(text.Length / 2);
    }
}

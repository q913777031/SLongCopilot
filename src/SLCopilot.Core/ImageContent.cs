using System.Text.Json.Nodes;

namespace SLCopilot.Core;

/// <summary>
/// 随提问发送的一张图片（界面截图、报错弹窗等），已由宿主缩放并编码。<see cref="Data"/> 为图片字节，<see cref="Name"/> 只用于界面与日志。
/// 构造时校验格式与大小：只接受 PNG / JPEG / GIF / WebP，单张不超过 <see cref="ImageContent.MaxBytes"/>。
/// </summary>
public sealed record ImageAttachment
{
    public ImageAttachment(string mediaType, byte[] data, int width, int height, string name)
    {
        if (!ImageContent.MediaTypes.Contains(mediaType)) throw new ArgumentException($"Unsupported image type {mediaType}; use PNG, JPEG, GIF or WebP.");
        if (data is null || data.Length == 0 || data.Length > ImageContent.MaxBytes)
            throw new ArgumentException($"An image must be between 1 byte and {ImageContent.MaxBytes / 1024 / 1024.0:0.##} MB after encoding.");
        MediaType = mediaType;
        Data = data;
        Width = width;
        Height = height;
        Name = string.IsNullOrWhiteSpace(name) ? "image" : name;
    }

    public string MediaType { get; }
    public byte[] Data { get; }
    public int Width { get; }
    public int Height { get; }
    public string Name { get; }
}

/// <summary>
/// 把图片放进服务商的消息格式。Claude 为 <c>{"type":"image","source":{"type":"base64",...}}</c> 块（放在文字之前），
/// OpenAI Chat Completions 为 <c>{"type":"image_url","image_url":{"url":"data:...;base64,..."}}</c>。
/// 保存对话时用 <see cref="WithoutImageData"/> 把图片换成文字占位，避免对话文件膨胀；重新打开的对话不再包含图片本身。
/// </summary>
public static class ImageContent
{
    /// <summary>单张图片编码后的上限：3.75 MB（Claude 对 base64 后 5 MB 的限制）。</summary>
    public const int MaxBytes = 3_932_160;
    /// <summary>每条消息最多附带的图片数。</summary>
    public const int MaxImagesPerMessage = 5;
    /// <summary>宿主缩放时的最长边（Claude 建议不超过 1568 像素；更大的图片会被服务端缩小而只增加上传量）。</summary>
    public const int MaxEdgePixels = 1568;
    /// <summary>估算上下文时每张图片计入的 token 数（约 1568×1568 / 750 的上限取整）。</summary>
    public const int EstimatedTokensPerImage = 1600;
    public const string OmittedText = "[An image was attached here; it is not kept in saved conversations.]";
    internal static readonly HashSet<string> MediaTypes = new(StringComparer.Ordinal) { "image/png", "image/jpeg", "image/gif", "image/webp" };

    /// <summary>用户消息的 content：没有图片时为字符串（与原格式相同），有图片时为内容块数组。</summary>
    /// <param name="responses">OpenAI Responses API 的内容部分（input_text / input_image），<paramref name="claude"/> 为 false 时有效。</param>
    public static JsonNode UserContent(bool claude, string text, IReadOnlyList<ImageAttachment>? images, bool responses = false)
    {
        if (images is not { Count: > 0 }) return text;
        if (images.Count > MaxImagesPerMessage) throw new ArgumentException($"Attach at most {MaxImagesPerMessage} images per message.");
        var blocks = new JsonArray();
        if (claude)
        {
            foreach (var image in images)
                blocks.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = image.MediaType, ["data"] = Convert.ToBase64String(image.Data) },
                });
            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }
        else if (responses)
        {
            blocks.Add(new JsonObject { ["type"] = "input_text", ["text"] = text });
            foreach (var image in images)
                blocks.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data)}" });
        }
        else
        {
            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
            foreach (var image in images)
                blocks.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data)}" },
                });
        }
        return blocks;
    }

    /// <summary>返回历史副本：图片块换成文字占位（Claude 为 text 块，OpenAI 为 text 部分），其余内容不变。</summary>
    public static JsonArray WithoutImageData(JsonArray messages)
    {
        var copy = (JsonArray)messages.DeepClone();
        foreach (var message in copy.OfType<JsonObject>())
            if (message["content"] is JsonArray blocks)
                for (var i = 0; i < blocks.Count; i++)
                    if (blocks[i] is JsonObject block && (string?)block["type"] is "image" or "image_url" or "input_image")
                        blocks[i] = new JsonObject { ["type"] = (string?)block["type"] == "input_image" ? "input_text" : "text", ["text"] = OmittedText };
        return copy;
    }

    /// <summary>估算上下文用的字符数：去掉图片的 base64 数据，每张图片按 <see cref="EstimatedTokensPerImage"/> 个 token（×3 字符）计入。</summary>
    public static int EstimateChars(JsonArray messages)
    {
        var images = 0;
        var dataChars = 0L;
        foreach (var message in messages.OfType<JsonObject>())
            if (message["content"] is JsonArray blocks)
                foreach (var block in blocks.OfType<JsonObject>())
                {
                    if ((string?)block["type"] == "image") { images++; dataChars += ((string?)block["source"]?["data"])?.Length ?? 0; }
                    else if ((string?)block["type"] == "image_url") { images++; dataChars += ((string?)block["image_url"]?["url"])?.Length ?? 0; }
                    else if ((string?)block["type"] == "input_image") { images++; dataChars += ((string?)block["image_url"])?.Length ?? 0; }
                }
        var total = messages.ToJsonString().Length - dataChars + (long)images * EstimatedTokensPerImage * 3;
        return (int)Math.Min(int.MaxValue, Math.Max(0, total));
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class ImageTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
    private static ImageAttachment Image(string name = "shot.png") => new("image/png", Png, 640, 480, name);

    [Fact]
    public void AttachmentsAreValidated()
    {
        Assert.Throws<ArgumentException>(() => new ImageAttachment("image/bmp", Png, 1, 1, "a.bmp"));
        Assert.Throws<ArgumentException>(() => new ImageAttachment("image/png", [], 1, 1, "a.png"));
        Assert.Throws<ArgumentException>(() => new ImageAttachment("image/png", new byte[ImageContent.MaxBytes + 1], 1, 1, "a.png"));
        Assert.Equal("image", new ImageAttachment("image/jpeg", Png, 1, 1, " ").Name);
    }

    [Fact]
    public void UserContentUsesEachProvidersImageFormat()
    {
        Assert.Equal("hello", (string?)ImageContent.UserContent(true, "hello", null));
        var claude = ImageContent.UserContent(true, "what is this?", [Image()]).AsArray();
        Assert.Equal("image", (string?)claude[0]!["type"]);
        Assert.Equal("base64", (string?)claude[0]!["source"]!["type"]);
        Assert.Equal("image/png", (string?)claude[0]!["source"]!["media_type"]);
        Assert.Equal(Convert.ToBase64String(Png), (string?)claude[0]!["source"]!["data"]);
        Assert.Equal("what is this?", (string?)claude[^1]!["text"]);
        var openAi = ImageContent.UserContent(false, "what is this?", [Image()]).AsArray();
        Assert.Equal("text", (string?)openAi[0]!["type"]);
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(Png), (string?)openAi[1]!["image_url"]!["url"]);
        Assert.Throws<ArgumentException>(() => ImageContent.UserContent(true, "x", Enumerable.Repeat(Image(), ImageContent.MaxImagesPerMessage + 1).ToList()));
    }

    [Fact]
    public void SavedHistoryDropsImageDataAndEstimatesIgnoreBase64()
    {
        var big = new ImageAttachment("image/png", new byte[3_000_000], 1568, 1000, "big.png");
        var history = new JsonArray(
            new JsonObject { ["role"] = "user", ["content"] = ImageContent.UserContent(true, "see", [big]) },
            new JsonObject { ["role"] = "user", ["content"] = ImageContent.UserContent(false, "see", [big]) });
        var saved = ImageContent.WithoutImageData(history);
        Assert.DoesNotContain("base64", saved.ToJsonString());
        Assert.Equal(ImageContent.OmittedText, (string?)saved[0]!["content"]![0]!["text"]);
        Assert.Equal("see", (string?)saved[1]!["content"]![0]!["text"]);
        Assert.Contains("base64", history.ToJsonString()); // 原历史不变。
        var estimate = ImageContent.EstimateChars(history);
        Assert.True(estimate < 20_000, $"estimate {estimate}");
        Assert.True(estimate >= 2 * ImageContent.EstimatedTokensPerImage * 3);
    }

    [Fact]
    public void ChatMessageImagesAreNotPersisted()
    {
        var json = JsonSerializer.Serialize(new ChatMessage("user", "hi") { Images = [Image()] });
        Assert.DoesNotContain("Images", json);
        Assert.Null(JsonSerializer.Deserialize<ChatMessage>(json)!.Images);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChatRequestsCarryImagesOnUserMessagesOnly(bool claude)
    {
        JsonNode? body = null;
        using var http = new HttpClient(new Capture(request => body = request, claude));
        var client = new AiClient(http);
        var messages = new List<ChatMessage>
        {
            new("user", "first") { Images = [Image()] },
            new("assistant", "answer") { Images = [Image()] },
            new("user", "second"),
        };
        await foreach (var _ in client.StreamAsync(new() { Provider = claude ? ProviderKind.Claude : ProviderKind.OpenAI }, "key", "system", messages)) { }
        var sent = body!["messages"]!.AsArray().Where(m => (string?)m!["role"] != "system").ToList();
        Assert.IsType<JsonArray>(sent[0]!["content"]);
        Assert.Equal("answer", (string?)sent[1]!["content"]);
        Assert.Equal("second", (string?)sent[2]!["content"]);
    }

    [Fact]
    public async Task AgentPutsImagesInTheRunsUserMessage()
    {
        var temp = Path.Combine(Path.GetTempPath(), "SLCopilot-image-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, "project"));
        try
        {
            JsonNode? body = null;
            using var http = new HttpClient(new Capture(request => body = request, claude: true));
            var conversation = new AgentConversation();
            await new AgentRunner(new AgentProtocol(http)).RunAsync(new() { Provider = ProviderKind.Claude }, "key", "Why does this dialog appear? [marker]",
                new WorkspaceTools(Path.Combine(temp, "project"), false, Path.Combine(temp, "backups")), _ => { }, CancellationToken.None,
                conversation: conversation, images: [Image()]);
            var user = body!["messages"]!.AsArray()[0]!;
            Assert.Equal("image", (string?)user["content"]![0]!["type"]);
            Assert.Contains("[marker]", (string?)user["content"]![1]!["text"]);
            // 带图片的用户消息为数组：项目指令是否已在历史中的判断仍然有效。
            Assert.True(conversation.HasUserMessageContaining("[marker]"));
            Assert.Contains("[attached an image]", ContextCompactor.AgentTranscript(conversation.Export()));
        }
        finally { Directory.Delete(temp, true); }
    }

    private sealed class Capture(Action<JsonNode> capture, bool claude) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
            var answer = claude
                ? "{\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":\"Done\"}]}"
                : "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"Done\"}}]}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sse.Convert(answer), Encoding.UTF8, "text/event-stream") };
        }
    }
}

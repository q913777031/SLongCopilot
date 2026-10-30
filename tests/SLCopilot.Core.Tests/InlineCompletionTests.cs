using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class InlineCompletionTests
{
    [Fact]
    public void FencesEchoedLinePrefixAndExistingTailAreRemoved()
    {
        Assert.Equal("Length;", InlineCompletion.Clean("```csharp\nvar n = s.Length;\n```", "    var n = s.", ""));
        Assert.Equal("x, y", InlineCompletion.Clean("x, y)", "Add(", ")"));
        Assert.Null(InlineCompletion.Clean("  \n ", "", ""));
    }

    [Fact]
    public void CompletionsAreLimitedToEightLines()
    {
        var raw = string.Join("\n", Enumerable.Range(1, 20).Select(i => "line" + i));
        Assert.Equal(InlineCompletion.MaxLines, InlineCompletion.Clean(raw, "", "")!.Split('\n').Length);
    }

    [Fact]
    public void PromptKeepsBoundedContextAroundTheCursor()
    {
        var prompt = InlineCompletion.BuildPrompt("A.cs", new string('a', 10_000) + "\nbefore", "after\n" + new string('b', 10_000));
        Assert.Contains("before⟦CURSOR⟧after", prompt);
        Assert.True(prompt.Length < InlineCompletion.MaxPrefixChars + InlineCompletion.MaxSuffixChars + 200);
    }

    [Fact]
    public async Task RequestUsesCompletionModelWithoutThinkingAndDoesNotRetry()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var settings = new ProviderSettings
        {
            Provider = ProviderKind.Claude, Endpoint = "https://api.anthropic.com/v1", Model = "claude-opus-5-5",
            CompletionModel = "claude-haiku-5-5", Effort = "high",
        };
        var text = await InlineCompletion.CompleteAsync(http, settings, "key", "A.cs", "int Add(int a, int b) => ", "", "int Add(int a, int b) => ", "", CancellationToken.None);
        Assert.Equal("a + b;", text);
        var body = JsonNode.Parse(handler.Bodies.Single())!;
        Assert.Equal("claude-haiku-5-5", (string?)body["model"]);
        Assert.Null(body["thinking"]);
        Assert.Equal(400, (int)body["max_tokens"]!);

        handler.Status = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<ProviderHttpException>(() =>
            InlineCompletion.CompleteAsync(http, settings, "key", "A.cs", "x", "", "x", "", CancellationToken.None));
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public void OldProfileFilesLoadWithCompletionsDisabled()
    {
        var catalog = JsonSerializer.Deserialize<ModelProfileCatalog>(JsonSerializer.Serialize(ModelProfileCatalog.Defaults()).Replace(",\"InlineCompletions\":false", ""))!;
        Assert.False(catalog.InlineCompletions);
        Assert.All(catalog.Profiles, p => Assert.Equal("", p.Settings.CompletionModel));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync());
            if (Status != HttpStatusCode.OK) return new HttpResponseMessage(Status);
            var sse = Sse.Convert("{\"content\":[{\"type\":\"text\",\"text\":\"a + b;\"}],\"stop_reason\":\"end_turn\"}");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
        }
    }
}

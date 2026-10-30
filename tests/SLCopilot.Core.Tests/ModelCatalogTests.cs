using System.Net;
using System.Text;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class ModelCatalogTests
{
    [Fact]
    public async Task OpenAiModelsAreListedSortedAndDeduplicated()
    {
        var handler = new Handler("{\"data\":[{\"id\":\"gpt-4.1\"},{\"id\":\"o3\"},{\"id\":\"gpt-4.1\"},{\"id\":\"\"},{\"object\":\"x\"}]}");
        using var http = new HttpClient(handler);
        var models = await ModelCatalog.ListAsync(http, new(), "key", CancellationToken.None);
        Assert.Equal(["gpt-4.1", "o3"], models);
        Assert.Equal("https://api.openai.com/v1/models", handler.Uri!.ToString());
        Assert.Equal("Bearer key", handler.Authorization);
    }

    [Fact]
    public async Task ClaudeModelsUseApiKeyHeaderAndLimit()
    {
        var handler = new Handler("{\"data\":[{\"id\":\"claude-opus-5-5\"}],\"has_more\":false}");
        using var http = new HttpClient(handler);
        var settings = new ProviderSettings { Provider = ProviderKind.Claude, Endpoint = "https://api.anthropic.com/v1", Model = "claude-opus-5-5" };
        var models = await ModelCatalog.ListAsync(http, settings, "key", CancellationToken.None);
        Assert.Equal(["claude-opus-5-5"], models);
        Assert.Equal("https://api.anthropic.com/v1/models?limit=1000", handler.Uri!.ToString());
        Assert.Equal("key", handler.ApiKey);
    }

    [Fact]
    public async Task ClaudeModelLimitsAreRead()
    {
        var handler = new Handler("{\"data\":[{\"id\":\"claude-opus-5-5\",\"max_input_tokens\":1000000,\"max_tokens\":128000},{\"id\":\"x\",\"max_input_tokens\":\"big\"}]}");
        using var http = new HttpClient(handler);
        var settings = new ProviderSettings { Provider = ProviderKind.Claude, Endpoint = "https://api.anthropic.com/v1", Model = "claude-opus-5-5" };
        var models = await ModelCatalog.ListDetailedAsync(http, settings, "key", CancellationToken.None);
        Assert.Equal(new ModelInfo("claude-opus-5-5", 1_000_000, 128_000), models[0]);
        Assert.Equal(new ModelInfo("x", null, null), models[1]);
    }

    [Fact]
    public void LegacyDefaultsAreUpgradedButCustomValuesAreKept()
    {
        Assert.Equal(1_000_000, ModelLimits.KnownContextWindow("claude-sonnet-5-5"));
        Assert.Equal(1_000_000, ModelLimits.KnownContextWindow("claude-opus-4-6"));
        Assert.Equal(200_000, ModelLimits.KnownContextWindow("claude-haiku-4-5"));
        Assert.Null(ModelLimits.KnownContextWindow("gpt-4.1"));
        Assert.Null(ModelLimits.KnownContextWindow("claude-opus-50"));
        var catalog = new ModelProfileCatalog
        {
            SelectedProfileId = "a",
            Profiles =
            [
                new("a", "A", new() { Provider = ProviderKind.Claude, Endpoint = "https://api.anthropic.com/v1", Model = "claude-opus-5-5", ContextWindow = 200_000, MaxOutputTokens = 16_000 }),
                new("b", "B", new() { Provider = ProviderKind.Claude, Endpoint = "https://api.anthropic.com/v1", Model = "claude-opus-5-5", ContextWindow = 300_000, MaxOutputTokens = 8_000 }),
                new("c", "C", new() { Model = "gpt-4.1", ContextWindow = 200_000 }),
                new("d", "D", new() { Provider = ProviderKind.Claude, Endpoint = "https://api.anthropic.com/v1", Model = "claude-3-7-sonnet", MaxOutputTokens = 16_000 }),
            ],
        }.UpgradeLegacyDefaults();
        Assert.Equal(16_000, catalog.Profiles[2].Settings.MaxOutputTokens);
        Assert.Equal(16_000, catalog.Profiles[3].Settings.MaxOutputTokens);
        Assert.Equal(16_000, new ProviderSettings().MaxOutputTokens);
        Assert.Equal(64_000, ModelProfileCatalog.Defaults().Profiles.Single(p => p.Id == "claude").Settings.MaxOutputTokens);
        Assert.Equal((1_000_000, 64_000), (catalog.Profiles[0].Settings.ContextWindow, catalog.Profiles[0].Settings.MaxOutputTokens));
        Assert.Equal((300_000, 8_000), (catalog.Profiles[1].Settings.ContextWindow, catalog.Profiles[1].Settings.MaxOutputTokens));
        Assert.Equal(200_000, catalog.Profiles[2].Settings.ContextWindow);
        Assert.Equal(32_768, new ProviderSettings { MaxOutputTokens = 64_000 }.OutputTokenLimit());
    }

    [Fact]
    public async Task FailuresAreReported()
    {
        using var unauthorized = new HttpClient(new Handler("", HttpStatusCode.Unauthorized));
        var error = await Assert.ThrowsAsync<ProviderHttpException>(() => ModelCatalog.ListAsync(unauthorized, new(), "key", CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized, error.Status);
        using var empty = new HttpClient(new Handler("{\"data\":[]}"));
        await Assert.ThrowsAsync<IOException>(() => ModelCatalog.ListAsync(empty, new(), "key", CancellationToken.None));
        using var html = new HttpClient(new Handler("<html>"));
        await Assert.ThrowsAnyAsync<Exception>(() => ModelCatalog.ListAsync(html, new(), "key", CancellationToken.None));
    }

    private sealed class Handler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public string? ApiKey { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            if (request.Headers.TryGetValues("x-api-key", out var keys)) ApiKey = keys.Single();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}

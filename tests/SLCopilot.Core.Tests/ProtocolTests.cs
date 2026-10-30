using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class ProtocolTests
{
    [Theory]
    [InlineData("http://example.com/v1")]
    [InlineData("file:///tmp/x")]
    [InlineData("https://key@example.com/v1")]
    [InlineData("https://example.com/v1?key=secret")]
    [InlineData("https://example.com/v1#fragment")]
    [InlineData("not a url")]
    public void UnsafeEndpointsAreRejected(string endpoint)
        => Assert.Throws<ArgumentException>(() => new ProviderSettings { Endpoint = endpoint }.RequestUri());

    [Theory]
    [InlineData("extreme")]
    [InlineData("HIGH ")]
    public void InvalidEffortIsRejected(string effort)
        => Assert.Throws<ArgumentException>(() => new ProviderSettings { Effort = effort }.RequestUri());

    [Fact]
    public void CustomEndpointPathIsPreserved()
        => Assert.Equal("https://example.com/proxy/v1/chat/completions",
            new ProviderSettings { Endpoint = "https://example.com/proxy/v1/" }.RequestUri().ToString());

    [Fact]
    public async Task SseHandlesCommentsCrLfAndMultilineData()
    {
        using var reader = new StringReader(": keepalive\r\nevent: message\r\ndata: first\r\ndata: second\r\n\r\n");
        var events = new List<string>();
        await foreach (var data in SseReader.ReadAsync(reader)) events.Add(data);
        Assert.Equal(["first\nsecond"], events);
    }

    [Fact]
    public async Task OversizedEventIsRejectedBeforeJsonParsing()
    {
        using var reader = new StringReader("data: " + new string('a', 70000));
        await Assert.ThrowsAsync<IOException>(async () => { await foreach (var _ in SseReader.ReadAsync(reader)) { } });
    }

    [Fact]
    public async Task UnterminatedEventIsNotEmitted()
    {
        using var reader = new StringReader("data: [DONE]\n");
        var count = 0;
        await foreach (var _ in SseReader.ReadAsync(reader)) count++;
        Assert.Equal(0, count);
    }

    [Fact]
    public void SettingsRoundTripContainsOnlyEnvironmentVariableName()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SLCopilot-tests-" + Guid.NewGuid());
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new SettingsStore(path);
            var settings = new ProviderSettings { KeyEnvironmentVariable = "TEST_AI_KEY", Model = "custom-model" };
            store.Save(settings);
            Assert.Equal(settings, store.Load());
            Assert.Contains("TEST_AI_KEY", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(directory)) Directory.Delete(directory); }
    }
}

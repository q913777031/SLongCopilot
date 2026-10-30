using System.Text.Json.Nodes;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class ParallelToolsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SLCopilot-parallel-" + Guid.NewGuid().ToString("N"));
    public ParallelToolsTests() => Directory.CreateDirectory(Path.Combine(root, "project"));
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    [Fact]
    public async Task ConsecutiveReadOnlyCallsRunConcurrentlyAndResultsKeepCallOrder()
    {
        var host = new SlowHost();
        var bodies = new List<JsonNode>();
        using var http = new HttpClient(new Handler(bodies));
        var workspace = new WorkspaceTools(Path.Combine(root, "project"), false, Path.Combine(root, "backups"), ide: host);
        var settings = new ProviderSettings { Provider = ProviderKind.Claude, Endpoint = "https://gateway.example.com/v1", Model = "claude-opus-5-5" };
        await new AgentRunner(new AgentProtocol(http)).RunAsync(settings, "key", "find things", workspace, _ => { }, CancellationToken.None);

        Assert.Equal(3, host.MaxActive);
        var results = bodies[1]["messages"]!.AsArray().Last()!["content"]!.AsArray();
        Assert.Equal(["t1", "t2", "t3"], results.Select(r => (string?)r!["tool_use_id"]));
        Assert.Contains("Alpha", (string?)results[0]!["content"]);
        Assert.Contains("Gamma", (string?)results[2]!["content"]);
    }

    private sealed class Handler(List<JsonNode> bodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync())!);
            var json = bodies.Count == 1
                ? "{\"content\":[" + string.Join(",", new[] { "Alpha", "Beta", "Gamma" }.Select((n, i) =>
                    "{\"type\":\"tool_use\",\"id\":\"t" + (i + 1) + "\",\"name\":\"find_symbol\",\"input\":{\"name\":\"" + n + "\"}}")) + "],\"stop_reason\":\"tool_use\"}"
                : "{\"content\":[{\"type\":\"text\",\"text\":\"done\"}],\"stop_reason\":\"end_turn\"}";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Sse.Convert(json), System.Text.Encoding.UTF8, "text/event-stream") };
        }
    }

    /// <summary>每次查找等到 3 个查找同时进行（或 2 秒超时）才返回，用来证明并发。</summary>
    private sealed class SlowHost : IIdeHost
    {
        private readonly TaskCompletionSource<bool> allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int active;
        public int MaxActive { get; private set; }

        public async Task<IReadOnlyList<CodeSymbol>> FindSymbolsAsync(string name, int maxItems, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref active);
            lock (this) MaxActive = Math.Max(MaxActive, now);
            if (now == 3) allStarted.TrySetResult(true);
            await Task.WhenAny(allStarted.Task, Task.Delay(2000, cancellationToken));
            Interlocked.Decrement(ref active);
            return [new("class", name, "/elsewhere/" + name + ".cs", 1, "class " + name)];
        }

        public Task<string?> GetUnsavedTextAsync(string fullPath, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<bool> TryWriteOpenDocumentAsync(string fullPath, string expectedText, string newText, TextFormat format, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<BuildOutcome> BuildSolutionAsync(string? projectPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<IdeDiagnostic>> GetDiagnosticsAsync(int maxItems, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IdeDiagnostic>>([]);
        public Task<IReadOnlyList<CodeSymbol>> FindReferencesAsync(string name, int maxItems, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CodeSymbol>>([]);
        public Task<IReadOnlyList<CodeSymbol>> GetOutlineAsync(string fullPath, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CodeSymbol>>([]);
        public Task<IReadOnlyList<CodeSymbol>> GetHierarchyAsync(string name, int maxItems, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CodeSymbol>>([]);
        public Task<IReadOnlyList<ProjectNode>> GetProjectGraphAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProjectNode>>([]);
        public Task<IReadOnlyList<SymbolSource>> ReadSymbolAsync(string name, int maxLines, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SymbolSource>>([]);
        public Task<IReadOnlyList<IdeDiagnostic>?> CheckFileAsync(string fullPath, string? text, int maxItems, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IdeDiagnostic>?>(null);
        public Task<IReadOnlyList<CodeSymbol>> GetCallersAsync(string name, int maxItems, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CodeSymbol>>([]);
        public Task<IReadOnlyList<CodeSymbol>> GetCalleesAsync(string name, int maxItems, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CodeSymbol>>([]);
        public Task<IReadOnlyList<CodeSymbol>> GetTypeDependenciesAsync(string name, int maxItems, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CodeSymbol>>([]);
    }
}

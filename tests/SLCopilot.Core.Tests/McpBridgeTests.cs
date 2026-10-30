using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class McpBridgeTests : IDisposable
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private readonly string temp = Path.Combine(Path.GetTempPath(), "SLCopilot-bridge-tests-" + Guid.NewGuid().ToString("N"));
    // These tests exercise a loopback server, including deliberately invalid Host headers.
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly List<McpBridgeServer> servers = [];
    private string Root => Path.Combine(temp, "project");

    public McpBridgeTests() => Directory.CreateDirectory(Root);

    public void Dispose()
    {
        foreach (var server in servers) server.Dispose();
        http.Dispose();
        try { Directory.Delete(temp, true); } catch (IOException) { }
    }

    private McpBridgeServer Start(bool writes = true, IIdeHost? ide = null, Func<string?>? root = null)
    {
        var server = new McpBridgeServer(new(FreePort(), Token, writes), _ => Task.FromResult(root is null ? Root : root()),
            (path, allowWrites) => new WorkspaceTools(path, allowWrites, Path.Combine(temp, "backups"), ide: ide));
        servers.Add(server);
        server.Start();
        return server;
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task<(HttpResponseMessage Response, JsonObject? Body)> PostAsync(McpBridgeServer server, object message, string? session = null,
        string? token = Token, string? host = null, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(message), Encoding.UTF8, "application/json"),
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (session is not null) request.Headers.Add("Mcp-Session-Id", session);
        if (host is not null) request.Headers.Host = host;
        if (origin is not null) request.Headers.Add("Origin", origin);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response, text.Length == 0 ? null : JsonNode.Parse(text) as JsonObject);
    }

    private async Task<string> InitializeAsync(McpBridgeServer server)
    {
        var (response, body) = await PostAsync(server, new { jsonrpc = "2.0", id = 1, method = "initialize",
            @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "claude-code", version = "2" } } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("2025-06-18", (string?)body!["result"]!["protocolVersion"]);
        return response.Headers.GetValues("Mcp-Session-Id").Single();
    }

    private async Task<JsonObject> CallToolAsync(McpBridgeServer server, string session, string name, object arguments, int id = 7)
    {
        var (response, body) = await PostAsync(server, new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name, arguments } }, session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (JsonObject)body!["result"]!;
    }

    [Fact]
    public async Task RequestsWithoutTheTokenOrFromBrowsersAreRejected()
    {
        var server = Start();
        var ping = new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } };
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(server, ping, token: null)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(server, ping, token: Token.Substring(1) + "0")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(server, ping, host: $"evil.example:{server.Port}")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(server, ping, origin: "https://evil.example")).Response.StatusCode);
        Assert.Equal(0, server.SessionCount);
    }

    [Fact]
    public async Task InitializeCreatesASessionAndListsWorkspaceTools()
    {
        var server = Start();
        var session = await InitializeAsync(server);
        Assert.Equal(1, server.SessionCount);
        var (_, body) = await PostAsync(server, new { jsonrpc = "2.0", id = 2, method = "tools/list" }, session);
        var tools = body!["result"]!["tools"]!.AsArray().Select(t => (string)t!["name"]!).ToList();
        Assert.Contains("read_file", tools);
        Assert.Contains("replace_symbol", tools);
        Assert.Contains("build_solution", tools);
        Assert.DoesNotContain("update_plan", tools);
        var read = body["result"]!["tools"]!.AsArray().Single(t => (string)t!["name"]! == "read_file")!;
        Assert.True((bool)read["annotations"]!["readOnlyHint"]!);
        Assert.Equal("object", (string?)read["inputSchema"]!["type"]);
    }

    [Fact]
    public async Task ReadOnlyBridgeDeclaresOnlyReadOnlyToolsAndRejectsOthers()
    {
        var server = Start(writes: false);
        var session = await InitializeAsync(server);
        var (_, list) = await PostAsync(server, new { jsonrpc = "2.0", id = 2, method = "tools/list" }, session);
        var tools = list!["result"]!["tools"]!.AsArray().Select(t => (string)t!["name"]!).ToList();
        Assert.Contains("read_symbol", tools);
        Assert.DoesNotContain("edit_file", tools);
        Assert.DoesNotContain("run_command", tools);
        var (_, call) = await PostAsync(server, new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "edit_file", arguments = new { } } }, session);
        Assert.Equal(-32602, (int)call!["error"]!["code"]!);
    }

    [Fact]
    public async Task ToolFailuresAreReportedAsErrors()
    {
        var server = Start();
        var session = await InitializeAsync(server);
        var result = await CallToolAsync(server, session, "get_errors", new { });
        Assert.True((bool)result["isError"]!);
        Assert.Contains("Error List is not available", (string)result["content"]![0]!["text"]!);
    }

    [Fact]
    public async Task CallsFailWhenNoSolutionIsOpen()
    {
        var server = Start(root: () => null);
        var session = await InitializeAsync(server);
        var result = await CallToolAsync(server, session, "find_symbol", new { name = "OrderService" });
        Assert.True((bool)result["isError"]!);
        Assert.Contains("No solution is open", (string)result["content"]![0]!["text"]!);
    }

    [Fact]
    public async Task ToolsRunAgainstTheIdeHost()
    {
        var ide = new BridgeHost { Diagnostics = [new("error", Path.Combine(Root, "A.cs"), 3, 5, "App", "CS1002: ; expected")] };
        var server = Start(ide: ide);
        var session = await InitializeAsync(server);
        var result = await CallToolAsync(server, session, "get_errors", new { });
        Assert.False((bool)result["isError"]!);
        var text = (string)result["content"]![0]!["text"]!;
        Assert.Contains("CS1002", text);
        Assert.Contains("A.cs", text);
    }

    [Fact]
    public async Task CancelledNotificationStopsTheCallWithAnError()
    {
        var ide = new BridgeHost { BlockBuild = true };
        var server = Start(ide: ide);
        var session = await InitializeAsync(server);
        var call = CallToolAsync(server, session, "build_solution", new { }, id: 42);
        await Within(ide.BuildStarted.Task);
        var (cancel, _) = await PostAsync(server, new { jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = 42 } }, session);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        var result = await Within(call);
        Assert.True((bool)result["isError"]!);
        Assert.Contains("cancelled", (string)result["content"]![0]!["text"]!);
    }

    [Fact]
    public async Task StopWaitsForRunningCallsAndRejectsNewOnes()
    {
        var ide = new BridgeHost { BlockBuild = true };
        var server = Start(ide: ide);
        var session = await InitializeAsync(server);
        var call = CallToolAsync(server, session, "build_solution", new { }, id: 5);
        await Within(ide.BuildStarted.Task);
        Assert.Equal(1, server.ActiveCalls);
        var stopped = server.StopAsync();
        var result = await Within(call);
        Assert.True((bool)result["isError"]!);
        Assert.Contains("stopped", (string)result["content"]![0]!["text"]!);
        await Within(stopped.ContinueWith(_ => true));
        Assert.Equal(0, server.ActiveCalls);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => PostAsync(server, new { jsonrpc = "2.0", id = 6, method = "ping" }, session));
    }

    [Fact]
    public async Task StopWithoutCallsCompletesImmediately()
    {
        var server = Start();
        await InitializeAsync(server);
        Assert.True(server.StopAsync().IsCompleted);
    }

    [Fact]
    public async Task UnknownAndDeletedSessionsAreRejected()
    {
        var server = Start();
        var (unknown, _) = await PostAsync(server, new { jsonrpc = "2.0", id = 2, method = "tools/list" }, "nope");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var (missing, _) = await PostAsync(server, new { jsonrpc = "2.0", id = 2, method = "tools/list" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var session = await InitializeAsync(server);
        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"http://127.0.0.1:{server.Port}/mcp");
        delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        delete.Headers.Add("Mcp-Session-Id", session);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(delete)).StatusCode);
        Assert.Equal(0, server.SessionCount);
        var (gone, _) = await PostAsync(server, new { jsonrpc = "2.0", id = 3, method = "ping" }, session);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task BatchesAndChunkedBodiesAreRejected()
    {
        var server = Start();
        var (batch, body) = await PostAsync(server, new object[] { new { jsonrpc = "2.0", id = 1, method = "ping" } });
        Assert.Equal(HttpStatusCode.BadRequest, batch.StatusCode);
        Assert.Equal(-32600, (int)body!["error"]!["code"]!);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = client.GetStream();
        var raw = Encoding.ASCII.GetBytes($"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n");
        await stream.WriteAsync(raw, 0, raw.Length);
        var reply = await new StreamReader(stream).ReadToEndAsync();
        Assert.StartsWith("HTTP/1.1 411", reply);
    }

    [Fact]
    public void SecondServerOnTheSamePortFailsToStart()
    {
        var first = Start();
        using var second = new McpBridgeServer(new(first.Port, Token, true), _ => Task.FromResult<string?>(Root),
            (path, writes) => new WorkspaceTools(path, writes, Path.Combine(temp, "backups")));
        Assert.Throws<SocketException>(second.Start);
    }

    [Fact]
    public void AbsolutePathsInsideTheWorkspaceBecomeRelative()
    {
        var inside = Path.Combine(Root, "src", "A.cs");
        var outside = Path.Combine(temp, "other", "B.cs");
        var arguments = JsonSerializer.SerializeToElement(new { path = inside, new_path = outside, query = Path.Combine(Root, "x") });
        var normalized = McpBridgeServer.NormalizePaths(arguments, Root);
        Assert.Equal("src/A.cs", normalized.GetProperty("path").GetString());
        Assert.Equal(outside, normalized.GetProperty("new_path").GetString());
        Assert.Equal(Path.Combine(Root, "x"), normalized.GetProperty("query").GetString());
        var relative = JsonSerializer.SerializeToElement(new { path = "src/A.cs" });
        Assert.Equal("src/A.cs", McpBridgeServer.NormalizePaths(relative, Root).GetProperty("path").GetString());
    }

    [Theory]
    [InlineData(80, Token)]
    [InlineData(47821, "short")]
    [InlineData(47821, "0123456789abcdef0123456789abcdef 0123456789abcdef")]
    public void InvalidOptionsAreRejected(int port, string token)
        => Assert.Throws<ArgumentException>(() => new McpBridgeOptions(port, token, true).Validate());

    [Fact]
    public void GeneratedTokensAreValid()
    {
        var token = McpBridgeOptions.NewToken();
        new McpBridgeOptions(McpBridgeOptions.DefaultPort, token, true).Validate();
        Assert.NotEqual(token, McpBridgeOptions.NewToken());
    }

    private static async Task<T> Within<T>(Task<T> task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))));
        return await task;
    }

    private sealed class BridgeHost : IIdeHost
    {
        public IdeDiagnostic[] Diagnostics { get; init; } = [];
        public bool BlockBuild { get; init; }
        public TaskCompletionSource<bool> BuildStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BuildOutcome> BuildSolutionAsync(string? projectPath, CancellationToken cancellationToken)
        {
            BuildStarted.TrySetResult(true);
            if (BlockBuild) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new(true, false);
        }

        public Task<IReadOnlyList<IdeDiagnostic>> GetDiagnosticsAsync(int maxItems, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IdeDiagnostic>>(Diagnostics.Take(maxItems).ToList());
        public Task<string?> GetUnsavedTextAsync(string fullPath, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<bool> TryWriteOpenDocumentAsync(string fullPath, string expectedText, string newText, TextFormat format, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<IReadOnlyList<CodeSymbol>> FindSymbolsAsync(string name, int maxItems, CancellationToken cancellationToken) => Empty<CodeSymbol>();
        public Task<IReadOnlyList<CodeSymbol>> FindReferencesAsync(string name, int maxItems, CancellationToken cancellationToken) => Empty<CodeSymbol>();
        public Task<IReadOnlyList<CodeSymbol>> GetOutlineAsync(string fullPath, CancellationToken cancellationToken) => Empty<CodeSymbol>();
        public Task<IReadOnlyList<CodeSymbol>> GetHierarchyAsync(string name, int maxItems, CancellationToken cancellationToken) => Empty<CodeSymbol>();
        public Task<IReadOnlyList<ProjectNode>> GetProjectGraphAsync(CancellationToken cancellationToken) => Empty<ProjectNode>();
        public Task<IReadOnlyList<IdeDiagnostic>?> CheckFileAsync(string fullPath, string? text, int maxItems, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IdeDiagnostic>?>(null);
        public Task<IReadOnlyList<SymbolSource>> ReadSymbolAsync(string name, int maxLines, CancellationToken cancellationToken) => Empty<SymbolSource>();
        public Task<IReadOnlyList<CodeSymbol>> GetCallersAsync(string name, int maxItems, CancellationToken cancellationToken) => Empty<CodeSymbol>();
        public Task<IReadOnlyList<CodeSymbol>> GetCalleesAsync(string name, int maxItems, CancellationToken cancellationToken) => Empty<CodeSymbol>();
        public Task<IReadOnlyList<CodeSymbol>> GetTypeDependenciesAsync(string name, int maxItems, CancellationToken cancellationToken) => Empty<CodeSymbol>();

        private static Task<IReadOnlyList<T>> Empty<T>() => Task.FromResult<IReadOnlyList<T>>([]);
    }
}

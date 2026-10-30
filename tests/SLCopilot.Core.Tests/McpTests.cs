using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class McpTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "SLCopilot-mcp-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly McpServerConfig Stdio = new("demo", "demo-server", [], new Dictionary<string, string>(), null, null, new Dictionary<string, string>(), 5, "test");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public McpTests() => Directory.CreateDirectory(temp);

    [Fact]
    public void ConfigAcceptsBothFormatsAndSkipsDisabledServers()
    {
        var vscode = McpConfigLoader.Parse("""
            // comments and trailing commas are allowed
            { "servers": {
                "sql": { "command": "dbtool", "args": ["--db", "${env:DB}"], "env": { "TOKEN": "${env:SQL_TOKEN}" }, "timeout_seconds": 30 },
                "docs": { "type": "http", "url": "https://mcp.example.com/mcp", "headers": { "Authorization": "Bearer ${env:DOCS}" } },
                "off": { "command": "x", "disabled": true },
            } }
            """, "a.json", true, "h1");
        Assert.Equal(new[] { "sql", "docs" }, vscode.Servers.Select(s => s.Name));
        Assert.False(vscode.Servers[0].IsHttp);
        Assert.Equal(30, vscode.Servers[0].TimeoutSeconds);
        Assert.True(vscode.Servers[1].IsHttp);
        var claude = McpConfigLoader.Parse("{\"mcpServers\":{\"fs\":{\"command\":\"server\"}}}", "b.json", false, "h2");
        Assert.Equal("fs", Assert.Single(claude.Servers).Name);
        var description = McpConfigLoader.Describe(vscode);
        Assert.Contains("dbtool --db ${env:DB}", description);
        Assert.Contains("环境变量：TOKEN", description);
        Assert.Contains("请求头：Authorization", description);
        Assert.DoesNotContain("Bearer", description);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"servers\":{\"bad name\":{\"command\":\"x\"}}}")]
    [InlineData("{\"servers\":{\"a\":{}}}")]
    [InlineData("{\"servers\":{\"a\":{\"command\":\"x\",\"url\":\"https://a\"}}}")]
    [InlineData("{\"servers\":{\"a\":{\"url\":\"http://example.com/mcp\"}}}")]
    [InlineData("{\"servers\":{\"a\":{\"command\":\"x\",\"args\":[1]}}}")]
    [InlineData("{\"servers\":{\"a\":{\"command\":\"x\",\"timeout_seconds\":0}}}")]
    [InlineData("not json")]
    public void InvalidConfigIsRejected(string json) => Assert.Throws<ArgumentException>(() => McpConfigLoader.Parse(json, "c.json", true, "h"));

    [Fact]
    public void LocalHttpIsAllowedAndEnvironmentReferencesExpand()
    {
        Assert.Single(McpConfigLoader.Parse("{\"servers\":{\"a\":{\"url\":\"http://localhost:3000/mcp\"}}}", "c.json", true, "h").Servers);
        Environment.SetEnvironmentVariable("SLCOPILOT_MCP_TEST", "value");
        try
        {
            Assert.Equal("Bearer value", McpConfigLoader.Expand("Bearer ${env:SLCOPILOT_MCP_TEST}"));
            Assert.Throws<ArgumentException>(() => McpConfigLoader.Expand("${env:SLCOPILOT_MCP_MISSING_VARIABLE}"));
        }
        finally { Environment.SetEnvironmentVariable("SLCOPILOT_MCP_TEST", null); }
    }

    [Fact]
    public async Task WorkspaceConfigIsFoundAndTrustFollowsTheContentHash()
    {
        var root = Path.Combine(temp, "ws");
        Directory.CreateDirectory(Path.Combine(root, ".slcopilot"));
        Assert.Null(McpConfigLoader.LoadWorkspace(root));
        await File.WriteAllTextAsync(Path.Combine(root, ".mcp.json"), "{\"mcpServers\":{\"a\":{\"command\":\"x\"}}}");
        var fallback = McpConfigLoader.LoadWorkspace(root)!;
        Assert.EndsWith(".mcp.json", fallback.Path);
        await File.WriteAllTextAsync(Path.Combine(root, ".slcopilot", "mcp.json"), "{\"servers\":{\"b\":{\"command\":\"y\"}}}");
        var preferred = McpConfigLoader.LoadWorkspace(root)!;
        Assert.Equal("b", Assert.Single(preferred.Servers).Name);

        var store = new McpTrustStore(Path.Combine(temp, "trust.json"));
        Assert.False(store.IsTrusted(root, preferred.Hash));
        store.Trust(root, preferred.Hash);
        Assert.True(new McpTrustStore(Path.Combine(temp, "trust.json")).IsTrusted(root + Path.DirectorySeparatorChar, preferred.Hash));
        await File.WriteAllTextAsync(Path.Combine(root, ".slcopilot", "mcp.json"), "{\"servers\":{\"b\":{\"command\":\"z\"}}}");
        Assert.False(store.IsTrusted(root, McpConfigLoader.LoadWorkspace(root)!.Hash));
        await File.WriteAllTextAsync(Path.Combine(temp, "trust.json"), "{broken");
        Assert.False(store.IsTrusted(root, preferred.Hash));
    }

    [Fact]
    public async Task StdioHandshakeListsAllPagesAndCallsTools()
    {
        using var server = new FakeServer(request => (string)request["method"]! switch
        {
            "initialize" => Ok(new JsonObject { ["protocolVersion"] = McpServerConnection.ProtocolVersion, ["serverInfo"] = new JsonObject { ["name"] = "fake", ["version"] = "1.0" } }),
            "tools/list" when request["params"]?["cursor"] is null => Ok(new JsonObject
            {
                ["tools"] = new JsonArray(Tool("query", readOnly: true)), ["nextCursor"] = "page2",
            }),
            "tools/list" => Ok(new JsonObject { ["tools"] = new JsonArray(Tool("update", readOnly: false)) }),
            "tools/call" => Ok(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "rows: " + request["params"]!["arguments"]!["sql"] }) }),
            _ => null,
        });
        using var connection = await McpServerConnection.ConnectAsync(Stdio, server.FromServer, server.ToServer, Timeout, CancellationToken.None);
        Assert.Equal("fake 1.0", connection.ServerInfo);
        Assert.Equal(new[] { "query", "update" }, connection.Tools.Select(t => t.Name));
        Assert.True(connection.Tools[0].ReadOnly);
        Assert.False(connection.Tools[1].ReadOnly);
        Assert.Contains(server.Received, m => (string?)m["method"] == "notifications/initialized");
        var result = await connection.CallToolAsync("query", JsonSerializer.SerializeToElement(new { sql = "select 1" }), CancellationToken.None);
        Assert.Equal("rows: select 1", McpSession.Format(result).Content);
    }

    [Fact]
    public async Task ServerErrorsPingsTimeoutsAndDisconnectsAreHandled()
    {
        using var server = new FakeServer(request => (string)request["method"]! switch
        {
            "initialize" => Ok(new JsonObject { ["protocolVersion"] = McpServerConnection.ProtocolVersion }),
            "tools/list" => Ok(new JsonObject { ["tools"] = new JsonArray(Tool("slow", false), Tool("broken", false)) }),
            "tools/call" when (string)request["params"]!["name"]! == "broken" => new JsonObject
                { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["error"] = new JsonObject { ["code"] = -32000, ["message"] = "database offline" } },
            _ => null, // slow：不回答
        }, pingFirst: true);
        using var connection = await McpServerConnection.ConnectAsync(Stdio with { TimeoutSeconds = 1 }, server.FromServer, server.ToServer, Timeout, CancellationToken.None);
        // 服务器在回答 initialize 之前先发来 ping：客户端必须回复。
        Assert.Contains(server.Received, m => m["result"] is JsonObject && (string?)m["id"] == "ping-1");
        var error = await Assert.ThrowsAsync<McpException>(() => connection.CallToolAsync("broken", JsonSerializer.SerializeToElement(new { }), CancellationToken.None));
        Assert.Contains("database offline", error.Message);
        var timeout = await Assert.ThrowsAsync<McpException>(() => connection.CallToolAsync("slow", JsonSerializer.SerializeToElement(new { }), CancellationToken.None));
        Assert.Contains("did not answer", timeout.Message);
        await WaitUntil(() => server.Received.Any(m => (string?)m["method"] == "notifications/cancelled"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.CallToolAsync("slow", JsonSerializer.SerializeToElement(new { }), cts.Token));
        server.Close();
        var closed = await Assert.ThrowsAsync<McpException>(() => connection.CallToolAsync("slow", JsonSerializer.SerializeToElement(new { }), CancellationToken.None));
        Assert.Contains("closed", closed.Message);
    }

    [Fact]
    public async Task SessionRequiresApprovalHonorsAllowForRunAndGatesReadOnlyRuns()
    {
        using var server = new FakeServer(request => (string)request["method"]! switch
        {
            "initialize" => Ok(new JsonObject { ["protocolVersion"] = McpServerConnection.ProtocolVersion }),
            "tools/list" => Ok(new JsonObject { ["tools"] = new JsonArray(Tool("query", true), Tool("update", false)) }),
            "tools/call" => Ok(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "ok" }) }),
            _ => null,
        });
        var connection = await McpServerConnection.ConnectAsync(Stdio, server.FromServer, server.ToServer, Timeout, CancellationToken.None);
        var answers = new Queue<(bool Approve, bool Allow)>([(false, false), (true, true), (true, false)]);
        var asked = new List<string>();
        using var session = new McpSession([connection], [], (approval, _) =>
        {
            asked.Add(approval.Title);
            var (approve, allow) = answers.Dequeue();
            approval.AllowForRun = allow;
            return Task.FromResult(approve);
        });
        Assert.Equal(new[] { "mcp__demo__query", "mcp__demo__update" }, session.Tools.Select(t => t.ExposedName));
        Assert.StartsWith("[External MCP server \"demo\", read-only.", session.Tools[0].Description);
        var args = JsonSerializer.SerializeToElement(new { id = 1 });

        var rejected = await session.CallAsync("mcp__demo__update", args, writesAllowed: true, "run", CancellationToken.None);
        Assert.True(rejected.IsError);
        Assert.Contains("rejected", rejected.Content);
        Assert.False((await session.CallAsync("mcp__demo__update", args, true, "run", CancellationToken.None)).IsError);
        Assert.False((await session.CallAsync("mcp__demo__update", args, true, "run", CancellationToken.None)).IsError);
        Assert.Equal(2, asked.Count);
        // 只读运行：非只读工具直接拒绝（不询问），只读工具仍需批准。
        var readOnly = await session.CallAsync("mcp__demo__update", args, writesAllowed: false, "run", CancellationToken.None);
        Assert.True(readOnly.IsError);
        Assert.Contains("Agent mode", readOnly.Content);
        Assert.Equal(2, asked.Count);
        session.BeginRun();
        Assert.False((await session.CallAsync("mcp__demo__query", args, false, "run", CancellationToken.None)).IsError);
        Assert.Equal(3, asked.Count);
        Assert.Equal(3, server.Received.Count(m => (string?)m["method"] == "tools/call"));
        Assert.True((await session.CallAsync("mcp__demo__missing", args, true, "run", CancellationToken.None)).IsError);
    }

    [Fact]
    public void ResultsAreFormattedAndNamesStayWithinLimits()
    {
        var result = McpSession.Format(JsonNode.Parse("""
            {"isError": true, "content": [
              {"type": "text", "text": "line"},
              {"type": "image", "mimeType": "image/png", "data": "AAAA"},
              {"type": "resource", "resource": {"uri": "file:///a.txt", "text": "file text"}},
              {"type": "resource_link", "name": "doc", "uri": "https://x"}
            ]}
            """)!.AsObject());
        Assert.True(result.IsError);
        Assert.Equal("line\n[image image/png omitted]\nfile text\n[resource link: doc https://x]", result.Content);
        Assert.Equal("{\"a\":1}", McpSession.Format(JsonNode.Parse("{\"content\":[],\"structuredContent\":{\"a\":1}}")!.AsObject()).Content);
        var big = McpSession.Format(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = new string('x', 60000) }) });
        Assert.Contains("[Result truncated", big.Content);

        Assert.Equal("mcp__srv__get_item_v2", McpSession.ExposeName("srv", "get.item v2"));
        var longName = McpSession.ExposeName("server", new string('a', 80));
        Assert.Equal(64, longName.Length);
        Assert.Equal(longName, McpSession.ExposeName("server", new string('a', 80)));
        Assert.NotEqual(longName, McpSession.ExposeName("server", new string('a', 79) + "b"));
    }

    [Fact]
    public async Task StreamableHttpKeepsTheSessionAndReadsEventStreams()
    {
        var requests = new List<(JsonObject Body, string? Session, string? Version)>();
        var handler = new LambdaHandler(request =>
        {
            var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().Result)!.AsObject();
            requests.Add((body, request.Headers.TryGetValues("Mcp-Session-Id", out var s) ? s.Single() : null,
                request.Headers.TryGetValues("MCP-Protocol-Version", out var v) ? v.Single() : null));
            Assert.Equal("Bearer secret", request.Headers.Authorization?.ToString());
            var method = (string)body["method"]!;
            if (!body.ContainsKey("id")) return new HttpResponseMessage(HttpStatusCode.Accepted);
            if (method == "initialize")
            {
                var response = Json(Ok(new JsonObject { ["protocolVersion"] = "2025-03-26" }, body["id"]));
                response.Headers.Add("Mcp-Session-Id", "session-1");
                return response;
            }
            var result = method == "tools/list"
                ? Ok(new JsonObject { ["tools"] = new JsonArray(Tool("search", true)) }, body["id"])
                : Ok(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "found" }) }, body["id"]);
            // 事件流中先有一条通知，再是响应。
            var stream = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{}}\n\n" +
                "event: message\ndata: " + result.ToJsonString() + "\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
        });
        using var http = new HttpClient(handler);
        var config = new McpServerConfig("web", null, [], new Dictionary<string, string>(), null, "https://mcp.example.com/mcp",
            new Dictionary<string, string> { ["Authorization"] = "Bearer secret" }, 5, "test");
        using var connection = await McpServerConnection.StartAsync(config, http, temp, [], "run", Timeout, CancellationToken.None);
        Assert.Equal("search", Assert.Single(connection.Tools).Name);
        var result = await connection.CallToolAsync("search", JsonSerializer.SerializeToElement(new { q = "x" }), CancellationToken.None);
        Assert.Equal("found", McpSession.Format(result).Content);
        Assert.Null(requests[0].Session);
        Assert.All(requests.Skip(1), r => Assert.Equal("session-1", r.Session));
        Assert.All(requests.Skip(1), r => Assert.Equal("2025-03-26", r.Version));
    }

    [Fact]
    public async Task HttpErrorsAndMissingProgramsFailToStartWithAReason()
    {
        using var http = new HttpClient(new LambdaHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var web = new McpServerConfig("web", null, [], new Dictionary<string, string>(), null, "https://mcp.example.com/mcp", new Dictionary<string, string>(), 5, "test");
        Assert.Contains("HTTP 401", (await Assert.ThrowsAsync<McpException>(() => McpServerConnection.StartAsync(web, http, temp, [], "run", Timeout, CancellationToken.None))).Message);
        var missing = Stdio with { Command = "slcopilot-no-such-mcp-server" };
        Assert.Contains("not found", (await Assert.ThrowsAsync<McpException>(() => McpServerConnection.StartAsync(missing, http, temp, [], "run", Timeout, CancellationToken.None))).Message);
        // 进程立即退出（不是 MCP 服务器）：握手失败并报告连接关闭。
        var exits = Stdio with { Command = "dotnet", Args = ["--version"] };
        var exited = await Assert.ThrowsAsync<McpException>(() => McpServerConnection.StartAsync(exits, http, temp, [], "run", Timeout, CancellationToken.None));
        Assert.Contains("closed", exited.Message);
    }

    [Fact]
    public async Task SessionStartReportsFailedServersAndKeepsTheOthers()
    {
        using var http = new HttpClient(new LambdaHandler(request =>
        {
            var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().Result)!.AsObject();
            if (!body.ContainsKey("id")) return new HttpResponseMessage(HttpStatusCode.Accepted);
            return Json((string)body["method"]! == "initialize"
                ? Ok(new JsonObject { ["protocolVersion"] = McpServerConnection.ProtocolVersion }, body["id"])
                : Ok(new JsonObject { ["tools"] = new JsonArray(Tool("search", true)) }, body["id"]));
        }));
        var web = new McpServerConfig("web", null, [], new Dictionary<string, string>(), null, "https://mcp.example.com/mcp", new Dictionary<string, string>(), 5, "test");
        using var session = await McpSession.StartAsync([web, Stdio with { Command = "slcopilot-no-such-mcp-server" }], http, temp, [],
            (_, _) => Task.FromResult(true), "run", CancellationToken.None);
        Assert.Equal("mcp__web__search", Assert.Single(session.Tools).ExposedName);
        Assert.Contains("web（1 个工具）", session.Servers);
        Assert.Contains(session.Notes, n => n.Contains("demo") && n.Contains("not found"));
    }

    [Fact]
    public async Task AgentDeclaresMcpToolsAndRoutesCallsToTheSession()
    {
        using var server = new FakeServer(request => (string)request["method"]! switch
        {
            "initialize" => Ok(new JsonObject { ["protocolVersion"] = McpServerConnection.ProtocolVersion }),
            "tools/list" => Ok(new JsonObject { ["tools"] = new JsonArray(Tool("query", true)) }),
            "tools/call" => Ok(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "42 rows" }) }),
            _ => null,
        });
        var connection = await McpServerConnection.ConnectAsync(Stdio, server.FromServer, server.ToServer, Timeout, CancellationToken.None);
        using var session = new McpSession([connection], [], (_, _) => Task.FromResult(true));
        var root = Path.Combine(temp, "project");
        Directory.CreateDirectory(root);
        var bodies = new List<JsonNode>();
        using var http = new HttpClient(new LambdaHandler(request =>
        {
            var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().Result)!;
            bodies.Add(body);
            var answer = bodies.Count == 1
                ? "{\"stop_reason\":\"tool_use\",\"content\":[{\"type\":\"tool_use\",\"id\":\"c1\",\"name\":\"mcp__demo__query\",\"input\":{\"sql\":\"select\"}}]}"
                : "{\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":\"Done\"}]}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sse.Convert(answer), Encoding.UTF8, "text/event-stream") };
        }));
        var events = new List<AgentEvent>();
        var runner = new AgentRunner(new AgentProtocol(http) { ExternalTools = session.Tools });
        await runner.RunAsync(new() { Provider = ProviderKind.Claude }, "key", "count rows", new WorkspaceTools(root, false, Path.Combine(temp, "backups")),
            events.Add, CancellationToken.None, mcp: session);
        var tools = bodies[0]["tools"]!.AsArray();
        Assert.Equal(28, tools.Count);
        Assert.Equal("mcp__demo__query", (string?)tools[^1]!["name"]);
        Assert.Equal("object", (string?)tools[^1]!["input_schema"]!["type"]);
        var toolResult = bodies[1]["messages"]!.AsArray()[^1]!["content"]![0]!;
        Assert.Equal("42 rows", (string?)toolResult["content"]);
        Assert.False((bool)toolResult["is_error"]!);
        Assert.Contains(events, e => e.Kind == "tool" && e.Message == "MCP demo / query");
        // 工具列表与会话不一致时拒绝运行。
        await Assert.ThrowsAsync<ArgumentException>(() => new AgentRunner(new AgentProtocol(http)).RunAsync(new() { Provider = ProviderKind.Claude }, "key", "x",
            new WorkspaceTools(root, false, Path.Combine(temp, "backups")), _ => { }, CancellationToken.None, mcp: session));
    }

    private static JsonObject Tool(string name, bool readOnly) => new()
    {
        ["name"] = name, ["description"] = name + " tool",
        ["inputSchema"] = new JsonObject { ["$schema"] = "https://json-schema.org/draft/2020-12/schema", ["type"] = "object", ["properties"] = new JsonObject() },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = readOnly },
    };

    private static JsonObject Ok(JsonObject result, JsonNode? id = null) => new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };

    private static HttpResponseMessage Json(JsonObject body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }

    /// <summary>进程内的 stdio MCP 服务器：经匿名管道收发一行一条的 JSON-RPC 消息。handler 返回 null 表示不回答。</summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly AnonymousPipeServerStream clientToServer = new(PipeDirection.Out);
        private readonly AnonymousPipeServerStream serverToClient = new(PipeDirection.In);
        private readonly AnonymousPipeClientStream serverReads;
        private readonly AnonymousPipeClientStream serverWrites;
        private readonly object gate = new();
        private readonly List<JsonObject> received = [];

        public FakeServer(Func<JsonObject, JsonObject?> handler, bool pingFirst = false)
        {
            serverReads = new(PipeDirection.In, clientToServer.ClientSafePipeHandle);
            serverWrites = new(PipeDirection.Out, serverToClient.ClientSafePipeHandle);
            _ = Task.Run(async () =>
            {
                using var reader = new StreamReader(serverReads, new UTF8Encoding(false));
                string? line;
                try
                {
                    while ((line = await reader.ReadLineAsync()) is not null)
                    {
                        var message = JsonNode.Parse(line)!.AsObject();
                        lock (gate) received.Add(message);
                        if (message["method"] is null || !message.ContainsKey("id")) continue;
                        if (pingFirst && (string?)message["method"] == "initialize")
                            Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "ping-1", ["method"] = "ping" });
                        if (handler(message) is { } response)
                        {
                            response["id"] = message["id"]!.DeepClone();
                            Write(response);
                        }
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            });
        }

        /// <summary>客户端读取服务器输出的流。</summary>
        public Stream FromServer => serverToClient;
        /// <summary>客户端写给服务器的流。</summary>
        public Stream ToServer => clientToServer;
        public IReadOnlyList<JsonObject> Received { get { lock (gate) return received.ToList(); } }

        private void Write(JsonObject message)
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
            lock (gate) { serverWrites.Write(bytes, 0, bytes.Length); serverWrites.Flush(); }
        }

        /// <summary>模拟服务器退出：关闭它的输出。</summary>
        public void Close() => serverWrites.Dispose();

        public void Dispose()
        {
            serverWrites.Dispose();
            serverReads.Dispose();
            clientToServer.Dispose();
            serverToClient.Dispose();
        }
    }

    private sealed class LambdaHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    public void Dispose()
    {
        try { Directory.Delete(temp, true); }
        catch (IOException) { }
    }
}

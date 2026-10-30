using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SLCopilot.Core;

/// <summary>MCP 服务器返回的 JSON-RPC 错误，或协议层面的失败（连接断开、超时、响应格式错误）。消息可直接返回给模型。</summary>
public sealed class McpException(string message) : IOException(message);

/// <summary>服务器声明的一个工具。<see cref="ReadOnly"/> 来自 annotations.readOnlyHint（服务器自述，不作为安全保证）。</summary>
public sealed record McpRemoteTool(string Name, string Description, JsonObject InputSchema, bool ReadOnly);

/// <summary>
/// JSON-RPC 2.0 请求 / 响应的公共部分：分配 id、超时与取消（取消时向服务器发送 notifications/cancelled）、把 error 转换为 <see cref="McpException"/>。
/// 子类负责把消息送达服务器并取回对应 id 的响应。
/// </summary>
internal abstract class McpTransport : IDisposable
{
    private long nextId;
    protected string ServerName { get; }

    protected McpTransport(string serverName) => ServerName = serverName;

    /// <summary>发送请求并等待结果（response.result）。超时抛出 <see cref="McpException"/>；取消抛出 OperationCanceledException。</summary>
    public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref nextId);
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null) request["params"] = parameters;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        JsonObject response;
        try { response = await ExchangeAsync(request, id, limit.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            // 通知服务器放弃该请求（尽力而为），再区分用户取消与超时。
            _ = NotifyQuietlyAsync("notifications/cancelled", new JsonObject { ["requestId"] = id, ["reason"] = cancellationToken.IsCancellationRequested ? "canceled" : "timeout" });
            cancellationToken.ThrowIfCancellationRequested();
            throw new McpException($"MCP server {ServerName} did not answer {method} within {timeout.TotalSeconds:0} seconds.");
        }
        if (response["error"] is JsonObject error)
            throw new McpException($"MCP server {ServerName} returned an error for {method}: {(string?)error["message"] ?? "unknown error"} (code {error["code"]?.ToJsonString() ?? "?"}).");
        return response["result"];
    }

    public abstract Task NotifyAsync(string method, JsonObject? parameters, CancellationToken cancellationToken);

    protected abstract Task<JsonObject> ExchangeAsync(JsonObject request, long id, CancellationToken cancellationToken);

    private async Task NotifyQuietlyAsync(string method, JsonObject parameters)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await NotifyAsync(method, parameters, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or HttpRequestException or ObjectDisposedException or InvalidOperationException) { }
    }

    protected static JsonObject Notification(string method, JsonObject? parameters)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null) message["params"] = parameters;
        return message;
    }

    public abstract void Dispose();
}

/// <summary>
/// stdio 传输：每条消息一行 UTF-8 JSON。读取循环把响应按 id 交给等待者；服务器发来的 ping 请求回复空结果，其他请求回复“方法不存在”。
/// 单条消息最长 8 MB；服务器关闭输出时所有等待中的请求以 <see cref="McpException"/> 结束。
/// </summary>
internal sealed class StdioTransport : McpTransport
{
    private const int MaxMessageBytes = 8 * 1024 * 1024;
    private readonly Stream input, output;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> pending = new();
    private volatile string? closed;

    public StdioTransport(string serverName, Stream fromServer, Stream toServer) : base(serverName)
    {
        input = fromServer;
        output = toServer;
        Reader = Task.Run(ReadLoopAsync);
    }

    /// <summary>读取循环；服务器关闭输出或读取失败时完成。</summary>
    public Task Reader { get; }

    public override Task NotifyAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
        => WriteAsync(Notification(method, parameters), cancellationToken);

    protected override async Task<JsonObject> ExchangeAsync(JsonObject request, long id, CancellationToken cancellationToken)
    {
        var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = waiter;
        try
        {
            if (closed is { } reason) throw new McpException(reason);
            await WriteAsync(request, cancellationToken).ConfigureAwait(false);
            using (cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken)))
                return await waiter.Task.ConfigureAwait(false);
        }
        finally { pending.TryRemove(id, out _); }
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await output.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new McpException($"Could not send to MCP server {ServerName}: {closed ?? ex.Message}");
        }
        finally { writeGate.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        var line = new MemoryStream();
        var buffer = new byte[16384];
        var reason = $"MCP server {ServerName} closed the connection.";
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                var start = 0;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    line.Write(buffer, start, i - start);
                    start = i + 1;
                    await HandleAsync(line.ToArray()).ConfigureAwait(false);
                    line.SetLength(0);
                }
                line.Write(buffer, start, read - start);
                if (line.Length > MaxMessageBytes) { reason = $"MCP server {ServerName} sent a message larger than 8 MB."; break; }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { reason = $"MCP server {ServerName} connection failed: {ex.Message}"; }
        closed = reason;
        foreach (var waiter in pending.Values) waiter.TrySetException(new McpException(reason));
    }

    private async Task HandleAsync(byte[] bytes)
    {
        JsonObject? message;
        try { message = JsonNode.Parse(Encoding.UTF8.GetString(bytes).Trim()) as JsonObject; }
        catch (JsonException) { return; } // 服务器在 stdout 上的非 JSON 输出（日志）：忽略。
        if (message is null) return;
        var hasId = message.TryGetPropertyValue("id", out var idNode) && idNode is JsonValue;
        if (message["method"] is JsonValue methodValue && methodValue.TryGetValue<string>(out var method))
        {
            if (!hasId) return; // 通知（日志、进度、列表变化）：不处理。工具列表在对话内固定。
            var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode!.DeepClone() };
            if (method == "ping") reply["result"] = new JsonObject();
            else reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not supported by SLCopilot." };
            try { await WriteAsync(reply, CancellationToken.None).ConfigureAwait(false); }
            catch (McpException) { }
            return;
        }
        if (hasId && ((JsonValue)idNode!).TryGetValue<long>(out var id) && pending.TryGetValue(id, out var waiter)) waiter.TrySetResult(message);
    }

    public override void Dispose()
    {
        try { output.Dispose(); }
        catch (IOException) { }
        try { input.Dispose(); }
        catch (IOException) { }
    }
}

/// <summary>
/// Streamable HTTP 传输：每条消息 POST 到同一地址；响应为 application/json 或 text/event-stream（读到对应 id 的响应为止）。
/// initialize 响应中的 Mcp-Session-Id 在之后的请求中带上。不打开服务器主动推送的 GET 流。
/// </summary>
internal sealed class HttpTransport(string serverName, HttpClient http, Uri url, IReadOnlyDictionary<string, string> headers) : McpTransport(serverName)
{
    private const int MaxEventChars = 8 * 1024 * 1024;
    private string? sessionId;
    private string? protocolVersion;

    public void SetProtocolVersion(string version) => protocolVersion = version;

    public override async Task NotifyAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(Notification(method, parameters), cancellationToken).ConfigureAwait(false);
    }

    protected override async Task<JsonObject> ExchangeAsync(JsonObject request, long id, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values) && values.FirstOrDefault() is { Length: > 0 } session) sessionId = session;
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (type.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            await foreach (var data in SseReader.ReadAsync(reader, cancellationToken, MaxEventChars).ConfigureAwait(false))
                if (Match(TryParse(data), id) is { } found) return found;
            throw new McpException($"MCP server {ServerName} ended the event stream without answering.");
        }
        var text = await Compat.ReadAsStringAsync(response.Content, cancellationToken).ConfigureAwait(false);
        return Match(TryParse(text), id) ?? throw new McpException($"MCP server {ServerName} returned no response for the request.");
    }

    private async Task<HttpResponseMessage> SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (sessionId is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        if (protocolVersion is not null) request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", protocolVersion);
        foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new McpException($"MCP server {ServerName} is unreachable: {ex.Message}"); }
        finally { request.Dispose(); }
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new McpException(status == 404 && sessionId is not null
                ? $"MCP server {ServerName} no longer recognizes this session (HTTP 404). Start a new conversation."
                : $"MCP server {ServerName} returned HTTP {status}.");
        }
        return response;
    }

    private static JsonNode? TryParse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    private static JsonObject? Match(JsonNode? node, long id) => node switch
    {
        JsonObject single when IsResponse(single, id) => single,
        JsonArray batch => batch.OfType<JsonObject>().FirstOrDefault(item => IsResponse(item, id)),
        _ => null,
    };

    private static bool IsResponse(JsonObject message, long id) =>
        message["id"] is JsonValue value && value.TryGetValue<long>(out var found) && found == id && (message.ContainsKey("result") || message.ContainsKey("error"));

    public override void Dispose() { }
}

/// <summary>
/// 与一个 MCP 服务器的连接：启动（stdio 进程或 HTTP 会话）、initialize 握手、读取工具列表，之后调用工具。
/// stdio 服务器进程放入作业对象，释放连接时结束整个进程树。启动失败时已启动的进程会被结束，并抛出 <see cref="McpException"/>（含 stderr 末尾）。
/// </summary>
public sealed class McpServerConnection : IDisposable
{
    public const string ProtocolVersion = "2025-06-18";
    private const int MaxToolsPerServer = 100, MaxStderrChars = 4000;
    private readonly McpTransport transport;
    private readonly Process? process;
    private readonly ProcessTree? tree;
    private readonly StringBuilder stderr = new();

    private McpServerConnection(McpServerConfig config, McpTransport transport, Process? process, ProcessTree? tree)
    {
        Config = config;
        this.transport = transport;
        this.process = process;
        this.tree = tree;
    }

    public McpServerConfig Config { get; }
    public IReadOnlyList<McpRemoteTool> Tools { get; private set; } = [];
    /// <summary>服务器自报的名称与版本（serverInfo），用于日志与界面。</summary>
    public string ServerInfo { get; private set; } = "";

    /// <summary>
    /// 启动并完成握手。<paramref name="workingDirectory"/> 为配置未指定 cwd 时的工作目录（相对 cwd 也以它为基准）。
    /// 子进程不继承服务商密钥变量（<paramref name="hiddenVariables"/>），再加上配置中的 env。
    /// </summary>
    public static async Task<McpServerConnection> StartAsync(McpServerConfig config, HttpClient http, string workingDirectory,
        IEnumerable<string> hiddenVariables, string runId, TimeSpan startupTimeout, CancellationToken cancellationToken)
    {
        McpServerConnection connection;
        try { connection = config.IsHttp ? StartHttp(config, http) : StartProcess(config, workingDirectory, hiddenVariables, runId); }
        catch (ArgumentException ex) { throw new McpException($"MCP server {config.Name}: {ex.Message}"); }
        var started = false;
        try
        {
            await connection.InitializeAsync(startupTimeout, cancellationToken).ConfigureAwait(false);
            RunLog.Info(runId, "mcp_started", ("server", config.Name), ("transport", config.IsHttp ? "http" : "stdio"), ("tools", connection.Tools.Count), ("info", connection.ServerInfo));
            started = true;
            return connection;
        }
        catch (Exception ex) when (ex is IOException or JsonException or HttpRequestException or InvalidOperationException)
        {
            var tail = connection.StderrTail();
            RunLog.Warn(runId, "mcp_start_failed", ("server", config.Name), ("error", ex.Message));
            throw new McpException((ex is McpException ? ex.Message : $"MCP server {config.Name} failed to start: {ex.Message}") +
                (tail.Length > 0 ? " Server output: " + tail : ""));
        }
        finally
        {
            // 启动失败或被取消：结束已启动的进程，不留下孤儿服务器。
            if (!started) connection.Dispose();
        }
    }

    /// <summary>在已有的流上建立 stdio 连接并握手（测试使用，不启动进程）。</summary>
    internal static async Task<McpServerConnection> ConnectAsync(McpServerConfig config, Stream fromServer, Stream toServer, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var connection = new McpServerConnection(config, new StdioTransport(config.Name, fromServer, toServer), null, null);
        await connection.InitializeAsync(timeout, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>调用工具，返回 result 对象。服务器错误、超时与断开抛出 <see cref="McpException"/>。</summary>
    public async Task<JsonObject> CallToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        var parameters = new JsonObject { ["name"] = name, ["arguments"] = JsonNode.Parse(arguments.GetRawText()) };
        return await transport.RequestAsync("tools/call", parameters, TimeSpan.FromSeconds(Config.TimeoutSeconds), cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new McpException($"MCP server {Config.Name} returned no result for {name}.");
    }

    private static McpServerConnection StartHttp(McpServerConfig config, HttpClient http)
    {
        var headers = config.Headers.ToDictionary(h => h.Key, h => McpConfigLoader.Expand(h.Value));
        return new(config, new HttpTransport(config.Name, http, new Uri(McpConfigLoader.Expand(config.Url!)), headers), null, null);
    }

    private static McpServerConnection StartProcess(McpServerConfig config, string workingDirectory, IEnumerable<string> hiddenVariables, string runId)
    {
        var directory = config.Cwd is null ? workingDirectory : Path.GetFullPath(Path.Combine(workingDirectory, McpConfigLoader.Expand(config.Cwd)));
        if (!Directory.Exists(directory)) throw new ArgumentException($"working directory {directory} does not exist.");
        var program = ResolveProgram(McpConfigLoader.Expand(config.Command!), directory);
        var start = new ProcessStartInfo(program, string.Join(" ", config.Args.Select(a => CommandRunner.Quote(McpConfigLoader.Expand(a)))))
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var name in hiddenVariables.Concat(["ANTHROPIC_API_KEY", "OPENAI_API_KEY"]))
            if (!string.IsNullOrWhiteSpace(name)) start.EnvironmentVariables.Remove(name.Trim());
        foreach (var pair in config.Env) start.EnvironmentVariables[pair.Key] = McpConfigLoader.Expand(pair.Value);
        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new ArgumentException($"could not start {program}.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            process.Dispose();
            throw new ArgumentException($"could not start {program}: {ex.Message}");
        }
        ProcessTree tree;
        try { tree = ProcessTree.Attach(process, runId); }
        catch (WorkspaceException ex)
        {
            process.Dispose();
            throw new ArgumentException(ex.Message);
        }
        var connection = new McpServerConnection(config, new StdioTransport(config.Name, process.StandardOutput.BaseStream, process.StandardInput.BaseStream), process, tree);
        _ = Task.Run(() => connection.PumpStderrAsync(process.StandardError.BaseStream));
        return connection;
    }

    /// <summary>
    /// 程序位置：含路径分隔符时按工作目录解析；否则按 PATH 查找（Windows 上只找 .exe，忽略相对 PATH 项）。
    /// .cmd / .bat（如 npx）需要写成 "command": "cmd", "args": ["/c", "npx", ...]。
    /// </summary>
    private static string ResolveProgram(string command, string directory)
    {
        if (command.IndexOfAny(['/', '\\']) >= 0)
        {
            var full = Path.GetFullPath(Path.Combine(directory, command));
            if (!File.Exists(full)) throw new ArgumentException($"program {full} does not exist.");
            return full;
        }
        var name = Compat.IsWindows && !command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? command + ".exe" : command;
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var folder = entry.Trim().Trim('"');
            if (folder.Length == 0 || !Compat.IsPathFullyQualified(folder)) continue;
            try
            {
                var candidate = Path.Combine(folder, name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        throw new ArgumentException(Compat.IsWindows
            ? $"{command} was not found on PATH as an .exe. For .cmd scripts such as npx use \"command\": \"cmd\", \"args\": [\"/c\", \"{command}\", ...]."
            : $"{command} was not found on PATH.");
    }

    private async Task InitializeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var version = typeof(McpServerConnection).Assembly.GetName().Version?.ToString(3) ?? "0";
        var result = await transport.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "SLCopilot", ["version"] = version },
        }, timeout, cancellationToken).ConfigureAwait(false) as JsonObject ?? throw new McpException($"MCP server {Config.Name} returned an invalid initialize result.");
        if (result["protocolVersion"] is JsonValue negotiated && negotiated.TryGetValue<string>(out var agreed) && transport is HttpTransport http) http.SetProtocolVersion(agreed);
        ServerInfo = result["serverInfo"] is JsonObject info ? $"{(string?)info["name"]} {(string?)info["version"]}".Trim() : "";
        await transport.NotifyAsync("notifications/initialized", null, cancellationToken).ConfigureAwait(false);
        var tools = new List<McpRemoteTool>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var list = await transport.RequestAsync("tools/list", cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor }, timeout, cancellationToken)
                .ConfigureAwait(false) as JsonObject;
            foreach (var item in (list?["tools"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (item["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name) || name.Length == 0) continue;
                var schema = item["inputSchema"] is JsonObject declared && (string?)declared["type"] == "object"
                    ? (JsonObject)declared.DeepClone() : new JsonObject { ["type"] = "object" };
                schema.Remove("$schema");
                var readOnly = item["annotations"] is JsonObject annotations && annotations["readOnlyHint"] is JsonValue hint && hint.TryGetValue<bool>(out var ro) && ro;
                tools.Add(new(name, (string?)item["description"] ?? "", schema, readOnly));
                if (tools.Count == MaxToolsPerServer) break;
            }
            cursor = (string?)list?["nextCursor"];
            if (cursor is null || tools.Count == MaxToolsPerServer) break;
        }
        Tools = tools;
    }

    private async Task PumpStderrAsync(Stream stream)
    {
        var buffer = new byte[4096];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                lock (stderr)
                {
                    stderr.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    if (stderr.Length > MaxStderrChars) stderr.Remove(0, stderr.Length - MaxStderrChars);
                }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private string StderrTail()
    {
        lock (stderr) { var text = stderr.ToString().Trim(); return text.Length > 1000 ? "…" + text.Substring(text.Length - 1000) : text; }
    }

    /// <summary>断开连接；stdio 服务器结束整个进程树。</summary>
    public void Dispose()
    {
        transport.Dispose();
        if (tree is not null)
        {
            tree.Kill();
            tree.Dispose();
        }
        process?.Dispose();
    }
}

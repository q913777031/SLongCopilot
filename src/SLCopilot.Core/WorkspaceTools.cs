using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SLCopilot.Core;

/// <summary>
/// 待用户确认的一次写入。<see cref="Diff"/> 为审批用差异预览；<see cref="Before"/> / <see cref="After"/> 为完整的修改前后文本，
/// 供宿主在差异窗口中展示。宿主批准时可把用户编辑后的内容写入 <see cref="Revised"/>，写入的将是该内容。
/// </summary>
public sealed record WriteProposal(string RelativePath, bool IsNewFile, string Diff, string Before = "", string After = "")
{
    /// <summary>用户在批准前修改过的内容；为 null 表示按模型的内容写入。只应在审批回调返回 true 之前设置。仅对 <see cref="ProposalKind.Write"/> 生效。</summary>
    public string? Revised { get; set; }
    /// <summary>改动类型。删除与移动不能在差异窗口中编辑。</summary>
    public ProposalKind Kind { get; init; } = ProposalKind.Write;
    /// <summary>移动时的目标相对路径；其他类型为 null。</summary>
    public string? Destination { get; init; }
    /// <summary>
    /// 用户批准时选择“本次运行内始终允许此文件”：同一运行中对同一路径的后续写入不再请求批准。只应在审批回调返回 true 之前设置；
    /// 仅对 <see cref="ProposalKind.Write"/> 生效，删除与移动始终逐次批准。不落盘，运行结束即失效。
    /// </summary>
    public bool AllowForRun { get; set; }
}

/// <summary>
/// 本次运行改动的一个文件。<see cref="Path"/> 为工作区相对路径（/ 分隔），<see cref="FullPath"/> 与 <see cref="BackupPath"/>（修改前的原始字节，
/// 新建文件为空）只供界面打开差异窗口，不发送给模型。<see cref="Kind"/> 为 created、modified、deleted 或 created_and_deleted；
/// <see cref="Complete"/> 为 false 表示写入未完成，应检查备份。
/// </summary>
public sealed record ChangedFile(string Path, string FullPath, string BackupPath, string Kind, int Added, int Removed, bool Complete);

/// <summary>待批准改动的类型：写入（新建或修改）、删除、移动 / 重命名。</summary>
public enum ProposalKind { Write, Delete, Move }

public sealed class WorkspaceTools
{
    private const int ApprovalPreviewChars = 200_000;
    /// <summary>默认单文件上限（整文件读取、写入、撤销）：1 MiB。</summary>
    public const int DefaultMaxFileBytes = 1024 * 1024;
    /// <summary>分段读取（read_file 带 start_line）允许的最大文件：32 MiB。</summary>
    public const int MaxRangedReadBytes = 32 * 1024 * 1024;
    /// <summary>不指定行段时最多返回的行数（与 Claude Code 的 Read 工具一致）；更长的文件返回开头部分并提示分段读取。</summary>
    public const int WholeReadMaxLines = 2000;
    private const int DefaultLineCount = 400, MaxLineCount = 5000, MaxListEntries = 1000, MaxListScanned = 5000;
    /// <summary>search_files 每批并行读取的文件数。</summary>
    private const int SearchReadBatch = 8;
    /// <summary>search_files 结果的总字符上限：结果会一直留在上下文中，超过时截断并提示缩小范围（200 行 × 300 字符可达 6 万字符）。</summary>
    private const int MaxSearchChars = 24_000;
    private const int MaxSearchResults = 200, MaxSearchResultsPerFile = 50, MaxSearchScanned = 50000, MaxFoundFiles = 500, MaxChangedFiles = 100;
    /// <summary>run_tests 默认超时（秒）：测试前要先编译，比普通命令长。</summary>
    private const int DefaultTestTimeoutSeconds = 600;
    /// <summary>变更记录中表示“该路径的文件已被删除”的最终哈希。</summary>
    private const string Absent = "absent";
    private readonly int maxFileBytes;
    private readonly WorkspacePolicy policy;
    private readonly TextCodec codec;
    private readonly IIdeHost? ide;
    private readonly CommandRunner? commands;
    private readonly Func<string, CancellationToken, Task> beforeWrite;
    private readonly Func<WriteProposal, CancellationToken, Task<bool>>? confirmWrite;
    // 只读工具可能并行执行（同时读取多个文件），读取版本表需线程安全。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> readVersions = new(StringComparer.OrdinalIgnoreCase);
    // 只读过一部分的文件（路径 → 当时的哈希）：不允许用 write_file 整体覆盖，以免模型按部分内容重写而截断文件；edit_file 不受影响。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> partialReads = new(StringComparer.OrdinalIgnoreCase);
    // 模型最近一次看到的文件内容（路径 → 哈希与磁盘文本）：文件随后被外部改动时，据此把差异告诉模型，而不必整篇重读。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Hash, string Text)> knownTexts = new(StringComparer.OrdinalIgnoreCase);
    // 本次运行中已把全文交给模型的文件（路径 → 当时的哈希）：内容未变时 read_file 只回复“未变化”，不重复发送全文。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> wholeReads = new(StringComparer.OrdinalIgnoreCase);
    // 最近一次写入的差异（相对路径 + 换行 + 差异），由 AfterWriteAsync 取走放入 ToolResult.Display；写入按顺序执行。
    private string? lastWriteDiff;
    private readonly Dictionary<string, Change> changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> previews = new(StringComparer.OrdinalIgnoreCase);
    // 每个改动文件相对备份（修改前）的新增 / 删除行数，随预览一起在写入后更新。
    private readonly Dictionary<string, (int Added, int Removed)> lineCounts = new(StringComparer.OrdinalIgnoreCase);
    // 本次运行中用户选择“始终允许”的文件（完整路径）。写入按顺序执行，不需要并发集合。
    private readonly HashSet<string> allowedForRun = new(StringComparer.OrdinalIgnoreCase);
    public bool AllowWrites { get; }
    public string BackupDirectory { get; }
    /// <summary>本次运行编号：备份目录名与运行日志中的 run 字段相同，便于对照。</summary>
    public string RunId { get; } = Guid.NewGuid().ToString("N");
    public int ChangeCount => changes.Count;
    public string Root => policy.Root;
    /// <summary>run_command / run_tests 可用的白名单；宿主未提供命令执行时为空。</summary>
    public IReadOnlyList<CommandRule> CommandRules => commands?.Rules ?? [];
    /// <summary>
    /// 某次写入因“本次运行内始终允许此文件”而未请求批准时触发，参数为相对路径。在工具执行线程上触发，订阅方负责切换线程。
    /// </summary>
    public event Action<string>? WriteAutoApproved;
    /// <summary>本次运行的写入保护规则（.slcopilot/settings.json，运行开始时读取）。</summary>
    public WriteProtection Protection { get; }
    /// <summary>按路径生效的指令（运行开始时读取）；第一次读写匹配的文件时附在工具结果中，每个指令每次运行只附一次。</summary>
    public IReadOnlyList<ScopedInstruction> ScopedInstructions => scopedInstructions;
    private readonly IReadOnlyList<ScopedInstruction> scopedInstructions;
    private readonly HashSet<string> appliedScoped = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="confirmWrite">每次写入、删除、移动前调用；返回 false 时不改动并把拒绝告知模型。为 null 时不需审批（仅测试使用）。</param>
    /// <param name="codec">文本编解码；为 null 时只支持 UTF-8 / UTF-16（带 BOM）。修改已有文件时保持其原编码与 BOM。</param>
    /// <param name="ide">IDE 能力；为 null 时不读编辑器缓冲区、始终写磁盘，编译与错误列表工具返回不可用。</param>
    /// <param name="commands">命令执行（白名单与审批）；为 null 时 run_command / run_tests 返回不可用。</param>
    public WorkspaceTools(string root, bool allowWrites, string backupRoot,
        Func<string, CancellationToken, Task>? beforeWrite = null,
        Func<WriteProposal, CancellationToken, Task<bool>>? confirmWrite = null,
        TextCodec? codec = null, IIdeHost? ide = null, int maxFileBytes = DefaultMaxFileBytes, CommandRunner? commands = null)
    {
        if (maxFileBytes is < 16 * 1024 or > MaxRangedReadBytes) throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        this.maxFileBytes = maxFileBytes;
        this.confirmWrite = confirmWrite;
        this.codec = codec ?? new TextCodec(null);
        this.ide = ide;
        this.commands = commands;
        policy = new(root);
        AllowWrites = allowWrites;
        BackupDirectory = Path.Combine(Path.GetFullPath(backupRoot), RunId);
        if (BackupDirectory.StartsWith(policy.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Backups must be stored outside the workspace.");
        this.beforeWrite = beforeWrite ?? ((_, _) => Task.CompletedTask);
        Protection = WriteProtection.Load(policy.Root);
        scopedInstructions = ScopedInstructionLoader.Load(policy.Root);
    }

    /// <summary>工具结果的 JSON 输出：中文等非 ASCII 字符原样输出（默认编码器会转成 \uXXXX，模型看到的是转义码，token 也成倍增加）。</summary>
    private static readonly System.Text.Json.Nodes.JsonNodeOptions NodeOptions = new();
    private static readonly JsonSerializerOptions ReadableJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>执行一个工具；结果中的 JSON 以可读形式输出（非 ASCII 字符不转义）。</summary>
    public async Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken token)
        => Readable(await ExecuteCoreAsync(name, arguments, token));

    /// <summary>把 JSON 结果重新输出为不转义非 ASCII 字符的形式；不是 JSON 或没有可转换的内容时原样返回。</summary>
    internal static ToolResult Readable(ToolResult result)
    {
        var content = result.Content;
        if (content.IndexOf("\\u", StringComparison.Ordinal) < 0 || content.Length == 0 || content[0] is not ('{' or '[')) return result;
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(content, NodeOptions);
            return node is null ? result : result with { Content = node.ToJsonString(ReadableJson) };
        }
        catch (JsonException) { return result; }
    }

    private async Task<ToolResult> ExecuteCoreAsync(string name, JsonElement arguments, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (name == "build_solution") return new(await BuildAsync(Optional(arguments, "project"), token));
            if (name == "get_errors") return new(await ErrorsAsync(Optional(arguments, "path"), OptionalBool(arguments, "errors_only") ?? false, token));
            if (name is "find_symbol" or "find_references") return new(await SymbolsAsync(name, Required(arguments, "name"), token));
            if (name == "get_hierarchy") return new(await HierarchyAsync(Required(arguments, "name"), token));
            if (name == "read_symbol")
            {
                var symbols = await ReadSymbolAsync(Required(arguments, "name"), token);
                return AttachScoped(new(symbols), PathsIn(symbols));
            }
            if (name == "check_file") return new(await CheckFileAsync(Required(arguments, "path"), token));
            if (name is "replace_symbol" or "insert_near_symbol")
            {
                var (edited, json) = await SymbolEditAsync(name, arguments, token);
                return AttachScoped(await AfterWriteAsync("edit_file", edited, new(json), token), [edited]);
            }
            if (name is "get_callers" or "get_callees" or "get_type_dependencies") return new(await RelationsAsync(name, Required(arguments, "name"), token));
            if (name == "get_project_graph") return new(await ProjectGraphAsync(token));
            if (name == "run_command") return new(await CommandAsync(arguments, token));
            if (name == "run_tests") return new(await TestsAsync(arguments, token));
            var path = Required(arguments, "path");
            var fileResult = await AfterWriteAsync(name, path, name switch
            {
                "list_directory" => new(List(path, OptionalInt(arguments, "depth") ?? 1, token)),
                "search_files" => new(await SearchAsync(path, Required(arguments, "query"), OptionalBool(arguments, "regex") ?? false, Optional(arguments, "glob"),
                    OptionalBool(arguments, "whole_word") ?? false, OptionalInt(arguments, "context") ?? 0, token)),
                "find_files" => new(await FindFilesAsync(path, Required(arguments, "pattern"), token)),
                "delete_file" => new(await DeleteAsync(path, Required(arguments, "expected_sha256"), token)),
                "move_file" => new(await MoveAsync(path, Required(arguments, "new_path"), Required(arguments, "expected_sha256"), token)),
                "read_file" => new(await ReadAsync(path, OptionalInt(arguments, "start_line"), OptionalInt(arguments, "line_count"), token)),
                "get_outline" => new(await OutlineAsync(path, token)),
                "write_file" => new(await WriteAsync(path, RejectLineNumbers(Required(arguments, "content", true)), Required(arguments, "expected_sha256"), token, wholeFile: true)),
                "edit_file" => new(await EditAsync(path, [new(Required(arguments, "old_text"), Required(arguments, "new_text", true), OptionalBool(arguments, "replace_all") ?? false)],
                    ExpectedVersion(arguments, path), token)),
                "multi_edit" => new(await EditAsync(path, Edits(arguments), ExpectedVersion(arguments, path), token)),
                "replace_between" => new(await ReplaceBetweenAsync(path, Required(arguments, "start_line"), Required(arguments, "end_line"),
                    Required(arguments, "new_text", true), Optional(arguments, "within"), ExpectedVersion(arguments, path), token)),
                _ => new ToolResult("Unknown tool. Use only the declared workspace tools.", true),
            }, token);
            return name is "read_file" or "write_file" or "edit_file" or "multi_edit" or "replace_between" or "get_outline" or "check_file"
                ? AttachScoped(fileResult, [path]) : fileResult;
        }
        catch (OperationCanceledException) { throw; }
        catch (WorkspaceException ex) { return new(ex.Message, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        { return new("File operation failed. Check the relative path, arguments, text encoding, permissions and file size.", true); }
    }

    /// <summary>
    /// replace_symbol / insert_near_symbol：按 Roslyn 找到的声明（含文档注释与特性）整段替换、删除，或在其前后插入新代码，不需要精确的 old_text。
    /// 名称必须恰好匹配一个声明（可带类型前缀与参数类型区分重载，如 OrderService.Save(int)）；VS 中的声明与磁盘内容不一致时拒绝（先保存或改用 edit_file）。
    /// 写入走与 edit_file 相同的审批、版本校验与备份。返回修改的相对路径与结果。
    /// </summary>
    private async Task<(string Path, string Result)> SymbolEditAsync(string tool, JsonElement arguments, CancellationToken token)
    {
        if (!AllowWrites) throw new WorkspaceException("This run is read-only. The user must enable writes before starting a new run.");
        if (ide is null) throw new WorkspaceException("Code navigation is not available in this host.");
        var name = Required(arguments, "name").Trim();
        if (name.Length > 300) throw new WorkspaceException("Symbol name must be at most 300 characters.");
        var replace = tool == "replace_symbol";
        var text = Required(arguments, replace ? "new_text" : "text", allowEmpty: replace);
        var position = replace ? null : (Optional(arguments, "position") ?? "after").ToLowerInvariant();
        if (position is not (null or "before" or "after")) throw new WorkspaceException("position must be before or after.");

        IEnumerable<SymbolSource> found = await ide.ReadSymbolAsync(name, 100_000, token);
        static string Compact(string value) => new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
        var paren = name.IndexOf('(');
        if (paren >= 0)
        {
            var wanted = Compact(name.Substring(paren));
            found = found.Where(f => f.Name.IndexOf('(') is var p and >= 0 && Compact(f.Name.Substring(p)) == wanted);
        }
        var matches = found.ToList();
        if (matches.Count == 0) throw new WorkspaceException($"No C#/VB declaration matches {name}. Check the exact name with find_symbol.");
        if (matches.Count > 1)
            throw new WorkspaceException($"{name} matches {matches.Count} declarations ({string.Join("; ", matches.Take(5).Select(m => $"{m.Name} at {DisplayFile(m.File)}:{m.StartLine}"))}). " +
                "Qualify it with the containing type or parameter types, e.g. OrderService.Save(int), or use edit_file.");
        var target = matches[0];
        if (!target.WholeLines)
            throw new WorkspaceException($"{target.Name} shares its line(s) with other code (for example int a, b;, an enum member or a record parameter), " +
                "so replacing or inserting by whole lines would change that code too. Use edit_file or replace_between instead.");
        string full, relative;
        try
        {
            full = Path.GetFullPath(target.File);
            relative = Compat.RelativeTo(Root, full).Replace('\\', '/');
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            throw new WorkspaceException("The declaration is outside the workspace and cannot be changed.");
        }
        policy.Resolve(relative);
        var bytes = await ReadBytesAsync(full, token);
        var hash = Hash(bytes);
        var current = codec.Decode(bytes).Text;
        var starts = new List<int> { 0 };
        for (var i = 0; i < current.Length; i++) if (current[i] == '\n') starts.Add(i + 1);
        if (target.StartLine < 1 || target.EndLine > starts.Count || target.EndLine < target.StartLine)
            throw new WorkspaceException("The declaration in Visual Studio no longer matches the file on disk. Save the file or read it with read_file and use edit_file.");
        var start = starts[target.StartLine - 1];
        var afterLine = target.EndLine < starts.Count ? starts[target.EndLine] : current.Length;
        var slice = current.Substring(start, afterLine - start);
        static string Normalized(string value) => value.Replace("\r\n", "\n").TrimEnd();
        if (Normalized(slice) != Normalized(target.Text))
            throw new WorkspaceException("The declaration in Visual Studio differs from the file on disk (unsaved changes or not yet reloaded). Save the file, or read it with read_file and use edit_file.");
        var newline = current.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
        var block = text.Length == 0 || text.EndsWith("\n", StringComparison.Ordinal) ? text : text + newline;
        if (afterLine == current.Length && !slice.EndsWith("\n", StringComparison.Ordinal) && block.Length > 0 && replace) block = block.TrimEnd('\r', '\n');
        var updated = replace ? current.Substring(0, start) + block + current.Substring(afterLine)
            : position == "before" ? current.Substring(0, start) + block + current.Substring(start)
            : current.Substring(0, afterLine) + (afterLine == current.Length && !current.EndsWith("\n", StringComparison.Ordinal) ? newline : "") + block + current.Substring(afterLine);
        // 模型可能只看过这个声明：没有完整读过当前版本时按部分读取处理，之后不能用 write_file 整体覆盖（WriteAsync 保留部分读取标记）。
        if (!readVersions.TryGetValue(full, out var known) || known != hash || partialReads.ContainsKey(full)) partialReads[full] = hash;
        readVersions[full] = hash;
        Remember(full, hash, current);
        var result = System.Text.Json.Nodes.JsonNode.Parse(await WriteAsync(relative, updated, hash, token))!.AsObject();
        result["symbol"] = target.Name;
        result["declaration_lines"] = $"{target.StartLine}-{target.EndLine}";
        if (text.Trim().Length > 0 && Snippet(updated, text.TrimEnd('\r', '\n')) is { } region) result["updated_regions"] = region;
        return (relative, result.ToJsonString());
    }

    /// <summary>check_file：按磁盘上的当前内容取单个 C#/VB 文件的编译器诊断，不编译整个解决方案。</summary>
    private async Task<string> CheckFileAsync(string path, CancellationToken token)
    {
        if (ide is null) throw new WorkspaceException("Code checking is not available in this host.");
        var full = policy.Resolve(path);
        var text = codec.Decode(await ReadBytesAsync(full, token)).Text;
        var diagnostics = await ide.CheckFileAsync(full, text, 100, token);
        if (diagnostics is null)
            return JsonSerializer.Serialize(new { path, note = "The file is not part of a C#/VB project loaded in Visual Studio. Use build_solution or get_errors instead." });
        return JsonSerializer.Serialize(new
        {
            path, errors = diagnostics.Count(d => d.Severity == "error"), warnings = diagnostics.Count(d => d.Severity == "warning"),
            diagnostics = MapDiagnostics(diagnostics), truncated = diagnostics.Count >= 100,
        });
    }

    /// <summary>
    /// 写入或修改 C#/VB 文件成功后，附带该文件的编译错误（最多 10 条，最多等待 10 秒；只是提示，失败时省略），模型无需编译即可发现明显错误。
    /// </summary>
    private async Task<ToolResult> AfterWriteAsync(string tool, string path, ToolResult result, CancellationToken token)
    {
        if (tool is "write_file" or "edit_file" or "multi_edit" or "replace_between" && Interlocked.Exchange(ref lastWriteDiff, null) is { } display && !result.IsError)
            result = result with { Display = display };
        if (result.IsError || ide is null || tool is not ("write_file" or "edit_file" or "multi_edit" or "replace_between")) return result;
        if (Path.GetExtension(path).ToLowerInvariant() is not (".cs" or ".vb")) return result;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            var full = policy.Resolve(path);
            var text = codec.Decode(await ReadBytesAsync(full, limit.Token)).Text;
            if (await ide.CheckFileAsync(full, text, 50, limit.Token) is not { } diagnostics) return result;
            var errors = diagnostics.Where(d => d.Severity == "error").Take(10).ToList();
            var node = System.Text.Json.Nodes.JsonNode.Parse(result.Content)!.AsObject();
            node["compile_errors"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(MapDiagnostics(errors)));
            if (errors.Count > 0) node["compile_note"] = "These compiler errors are in the file after your change (other files are not checked). Fix them before continuing.";
            return result with { Content = node.ToJsonString() };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return result; }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or ArgumentException)
        {
            RunLog.Warn(RunId, "check_after_write_failed", ("path", path), ("error", ex.GetType().Name));
            return result;
        }
    }

    /// <summary>
    /// 列出目录内容：<paramref name="depth"/> 为 1 时只列直接子项，最大 4 时按目录树逐层列出（目录在前，按名称排序）。
    /// 跳过工作区策略排除的项；最多 1000 项、扫描 5000 项，超出时末尾注明截断。
    /// </summary>
    private string List(string path, int depth, CancellationToken token)
    {
        if (depth is < 1 or > 4) throw new WorkspaceException("depth must be between 1 and 4.");
        var full = policy.Resolve(path, directory: true);
        var entries = new List<string>();
        var scanned = 0;
        var truncated = false;
        void Walk(string directory, int level)
        {
            IEnumerable<string> children = Directory.EnumerateFileSystemEntries(directory)
                .OrderBy(e => Directory.Exists(e) ? 0 : 1).ThenBy(e => e, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in children)
            {
                token.ThrowIfCancellationRequested();
                if (++scanned > MaxListScanned || entries.Count == MaxListEntries) { truncated = true; return; }
                var relative = Compat.RelativeTo(Root, entry).Replace('\\', '/');
                var isDirectory = Directory.Exists(entry);
                try { policy.Resolve(relative, isDirectory); }
                catch (IOException) { continue; }
                entries.Add(relative + (isDirectory ? "/" : ""));
                if (isDirectory && level < depth) Walk(entry, level + 1);
                if (truncated) return;
            }
        }
        Walk(full, 1);
        if (truncated) entries.Add("[Listing truncated]");
        return JsonSerializer.Serialize(entries);
    }

    /// <summary>
    /// 递归搜索文本文件的行：默认按字面文本（忽略大小写），<paramref name="regex"/> 为 true 时按 .NET 正则逐行匹配（忽略大小写，单次匹配 200 毫秒上限）。
    /// <paramref name="glob"/> 限定文件（不含 / 时匹配文件名，否则匹配相对 <paramref name="path"/> 的路径）。最多 200 行，每个文件最多 50 行。
    /// </summary>
    private async Task<string> SearchAsync(string path, string query, bool regex, string? glob, bool wholeWord, int context, CancellationToken token)
    {
        if (query.Length > 500) throw new WorkspaceException("Search text must be at most 500 characters.");
        if (context is < 0 or > 3) throw new WorkspaceException("context must be between 0 and 3.");
        Regex? pattern = null;
        if (regex || wholeWord)
        {
            var expression = regex ? query : Regex.Escape(query);
            if (wholeWord) expression = $@"\b(?:{expression})\b";
            try { pattern = new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
            catch (ArgumentException ex) { throw new WorkspaceException("Invalid regular expression: " + ex.Message); }
        }
        var filter = glob is null ? null : GlobFilter(glob);
        var start = policy.Resolve(path, true);
        var results = new List<string>();
        var resultChars = 0;
        var truncated = false;
        var candidates = new List<(string Full, string Relative)>();
        await WalkFilesAsync(start, (full, relative) =>
        {
            if (filter is null || filter(full, start)) candidates.Add((full, relative));
            return Task.FromResult(true);
        }, () => truncated = true, token);
        try
        {
            // 每批并行读取若干文件（读取与解密是主要耗时），按遍历顺序逐个匹配，结果与顺序读取相同。
            for (var batch = 0; batch < candidates.Count; batch += SearchReadBatch)
            {
                var group = candidates.Skip(batch).Take(SearchReadBatch).ToList();
                var texts = await Task.WhenAll(group.Select(c => ReadForSearchAsync(c.Full, token))).ConfigureAwait(false);
                for (var f = 0; f < group.Count; f++)
                {
                    if (texts[f] is not { } content) continue;
                    if (!MatchFile(group[f].Relative, content)) return JsonSerializer.Serialize(new { matches = results, truncated = true });
                }
            }
        }
        catch (RegexMatchTimeoutException) { throw new WorkspaceException("The regular expression is too slow on this content. Simplify it or narrow path / glob."); }
        return JsonSerializer.Serialize(new { matches = results, truncated });

        // 返回 false 表示结果已满。
        bool MatchFile(string relative, string content)
        {
            var lines = content.Split('\n');
            for (int i = 0, inFile = 0; i < lines.Length && inFile < MaxSearchResultsPerFile; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (pattern?.IsMatch(line) ?? line.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (results.Count == MaxSearchResults || resultChars >= MaxSearchChars) return false;
                    var match = $"{relative}:{i + 1}: {line[..Math.Min(line.Length, 300)]}";
                    if (context > 0)
                    {
                        // 上下文行用 “路径-行号-” 标记，与 grep -C 一致。
                        var around = new List<string>();
                        for (var k = Math.Max(0, i - context); k <= Math.Min(lines.Length - 1, i + context); k++)
                        {
                            var text = lines[k].TrimEnd('\r');
                            around.Add(k == i ? match : $"{relative}-{k + 1}- {text[..Math.Min(text.Length, 300)]}");
                        }
                        match = string.Join("\n", around);
                    }
                    results.Add(match);
                    resultChars += match.Length;
                    inFile++;
                }
            }
            return true;
        }
    }

    /// <summary>读取并解码一个待搜索的文件；读不了（过大、被占用、无权限、无法解码）时返回 null，与此前跳过该文件相同。</summary>
    private async Task<string?> ReadForSearchAsync(string full, CancellationToken token)
    {
        try { return codec.Decode(await ReadBytesAsync(full, token).ConfigureAwait(false)).Text; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException) { return null; }
    }

    /// <summary>按通配模式查找文件（不含 / 时匹配文件名，否则匹配相对 <paramref name="path"/> 的路径），最多 500 个，按路径排序。</summary>
    private async Task<string> FindFilesAsync(string path, string pattern, CancellationToken token)
    {
        var filter = GlobFilter(pattern);
        var start = policy.Resolve(path, true);
        var files = new List<string>();
        var truncated = false;
        await WalkFilesAsync(start, (full, relative) =>
        {
            if (!filter(full, start)) return Task.FromResult(true);
            if (files.Count == MaxFoundFiles) { truncated = true; return Task.FromResult(false); }
            files.Add(relative);
            return Task.FromResult(true);
        }, () => truncated = true, token);
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return JsonSerializer.Serialize(new { files, truncated });
    }

    /// <summary>
    /// 深度优先遍历 <paramref name="start"/> 下工作区策略允许访问的文件（最多 32 层、扫描 50,000 项），对每个文件调用 <paramref name="visit"/>；
    /// visit 返回 false 时停止。扫描数达到上限时调用 <paramref name="limitReached"/> 并停止。
    /// </summary>
    private async Task WalkFilesAsync(string start, Func<string, string, Task<bool>> visit, Action limitReached, CancellationToken token)
    {
        var stack = new Stack<(string Directory, int Depth)>();
        stack.Push((start, 0));
        var scanned = 0;
        while (stack.Count > 0)
        {
            var (directory, depth) = stack.Pop();
            // 属性来自目录枚举本身，不再对每一项单独访问文件系统（透明加密等文件系统过滤驱动下每次访问都较慢）。
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if (++scanned > MaxSearchScanned) { limitReached(); return; }
                var relative = Compat.RelativeTo(Root, entry.FullName).Replace('\\', '/');
                if (!policy.AllowsWalkEntry(relative, entry.Attributes)) continue;
                if ((entry.Attributes & FileAttributes.Directory) != 0) { if (depth < 32) stack.Push((entry.FullName, depth + 1)); continue; }
                if (!await visit(entry.FullName, relative)) return;
            }
        }
    }

    /// <summary>通配模式转为文件筛选：不含 / 时匹配文件名，否则匹配相对搜索目录的路径。* 与 ? 不跨目录，** 匹配任意层。</summary>
    private static Func<string, string, bool> GlobFilter(string glob)
    {
        var pattern = glob.Trim().Replace('\\', '/');
        while (pattern.StartsWith("./", StringComparison.Ordinal)) pattern = pattern.Substring(2);
        pattern = pattern.TrimStart('/');
        if (pattern.Length is 0 or > 200) throw new WorkspaceException("Use a nonempty file pattern of at most 200 characters, such as *.cs or src/**/*.xaml.");
        var regex = new Regex("^" + WorkspacePolicy.GlobExpression(pattern) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return pattern.IndexOf('/') < 0
            ? (full, _) => regex.IsMatch(Path.GetFileName(full))
            : (full, start) => regex.IsMatch(Compat.RelativeTo(start, full).Replace('\\', '/'));
    }

    /// <summary>
    /// 读取文件。不带 <paramref name="startLine"/> 时读取全文（不超过单文件上限）；带时只返回从该行（从 1 开始）起的
    /// <paramref name="lineCount"/> 行（默认 400，最多 5000），文件可大到 32 MiB。两种方式都返回整个文件的 SHA-256 与总行数。
    /// </summary>
    private async Task<string> ReadAsync(string path, int? startLine, int? lineCount, CancellationToken token)
    {
        var full = policy.Resolve(path);
        var ranged = startLine is not null || lineCount is not null;
        if (startLine is < 1) throw new WorkspaceException("start_line must be 1 or greater.");
        if (lineCount is < 1 or > MaxLineCount) throw new WorkspaceException($"line_count must be between 1 and {MaxLineCount}.");
        if (!ranged && File.Exists(full) && new FileInfo(full).Length is var length && length > maxFileBytes && length <= MaxRangedReadBytes)
            throw new WorkspaceException($"File is larger than {maxFileBytes / 1024} KiB. Read it in parts with start_line and line_count, or use get_outline / search_files to find the relevant lines.");
        var bytes = await ReadBytesAsync(full, token, ranged ? MaxRangedReadBytes : maxFileBytes);
        var (text, format) = codec.Decode(bytes);
        var hash = Hash(bytes);
        readVersions[full] = hash;
        if (bytes.Length <= maxFileBytes) Remember(full, hash, text);
        string? unsaved = null;
        if (ide is not null && await ide.GetUnsavedTextAsync(full, token) is { } buffer)
        {
            if (!ranged && buffer.Length > maxFileBytes) throw new WorkspaceException($"The editor buffer is larger than {maxFileBytes / 1024} KiB. Read it in parts with start_line and line_count.");
            unsaved = buffer;
        }
        var source = unsaved ?? text;
        var longFile = !ranged && CountLines(source) > WholeReadMaxLines;
        if (!ranged && !longFile)
        {
            var whole = source.Split('\n');
            var wholeCount = source.EndsWith("\n", StringComparison.Ordinal) ? whole.Length - 1 : whole.Length;
            var (numbered, shortened) = Numbered(whole, 0, wholeCount);
            if (unsaved is null && shortened == 0)
            {
                if (DeduplicateReads && wholeReads.TryGetValue(full, out var seen) && seen == hash)
                    return JsonSerializer.Serialize(new
                    {
                        path, sha256 = hash, total_lines = wholeCount, unchanged = true,
                        note = "Unchanged since you read the whole file earlier in this run; that content and its line numbers are still current. Do not read it again.",
                    });
                wholeReads[full] = hash;
            }
            else wholeReads.TryRemove(full, out _);
            // 有被截短的超长行时模型没有看到完整内容：按部分读取处理，不允许 write_file 整体覆盖。
            if (shortened > 0) partialReads[full] = hash;
            else partialReads.TryRemove(full, out _);
            var result = new System.Text.Json.Nodes.JsonObject { ["path"] = path, ["sha256"] = hash, ["encoding"] = format.Name, ["total_lines"] = wholeCount };
            if (unsaved is not null)
            {
                result["unsaved_editor_changes"] = true;
                result["note"] = "Content is the unsaved editor buffer, which differs from the file on disk. The file cannot be modified until the user saves it.";
            }
            else if (shortened > 0) result["note"] = ShortenedNote(shortened);
            result["content"] = numbered;
            return result.ToJsonString();
        }

        var lines = source.Split('\n');
        var totalLines = source.EndsWith("\n", StringComparison.Ordinal) ? lines.Length - 1 : lines.Length;
        var first = startLine ?? 1;
        var count = lineCount ?? (longFile ? WholeReadMaxLines : DefaultLineCount);
        var builder = new StringBuilder();
        var last = first - 1;
        var cut = 0;
        for (var i = first - 1; i < Math.Min(totalLines, first - 1 + count); i++)
        {
            var line = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t" + Shorten(lines[i].TrimEnd('\r'), ref cut) + "\n";
            if (builder.Length + line.Length > maxFileBytes) break;
            builder.Append(line);
            last = i + 1;
        }
        if (first == 1 && last >= totalLines && cut == 0) partialReads.TryRemove(full, out _);
        else partialReads[full] = hash;
        return JsonSerializer.Serialize(new
        {
            path, sha256 = hash, encoding = format.Name, total_lines = totalLines, start_line = first, end_line = last,
            unsaved_editor_changes = unsaved is not null ? true : (bool?)null,
            note = longFile
                ? $"The file has {totalLines} lines; only lines 1-{last} are shown to save tokens. Read further parts with start_line and line_count. Use edit_file, not write_file, on a file you have not read completely."
                : last < Math.Min(totalLines, first - 1 + count) ? "Stopped early at the size limit; continue from end_line + 1."
                : cut > 0 ? ShortenedNote(cut) : null,
            content = builder.ToString().TrimEnd('\n'),
        });
    }

    /// <summary>read_file 的显示格式：每行为“行号 + 制表符 + 内容”（行号从 1 开始，去掉行尾 \r）。模型修改时不应把前缀带入文本。</summary>
    private static (string Text, int Shortened) Numbered(string[] lines, int from, int count)
    {
        var builder = new StringBuilder();
        var shortened = 0;
        for (var i = from; i < count; i++)
        {
            if (i > from) builder.Append('\n');
            builder.Append(i + 1).Append('\t').Append(Shorten(lines[i].TrimEnd('\r'), ref shortened));
        }
        return (builder.ToString(), shortened);
    }

    /// <summary>read_file 中单行显示的最大字符数；更长的行（压缩过的脚本、生成代码、内嵌数据）截短显示，避免占满上下文。</summary>
    private const int MaxDisplayLineChars = 2000;

    private static string Shorten(string line, ref int shortened)
    {
        if (line.Length <= MaxDisplayLineChars) return line;
        shortened++;
        return line.Substring(0, MaxDisplayLineChars) + $" [... line shortened, {line.Length:N0} characters in total]";
    }

    private static string ShortenedNote(int count) =>
        $"{count} line(s) longer than {MaxDisplayLineChars} characters are shortened in content. Edit only text you can see with edit_file; write_file cannot replace this file.";

    private static readonly Regex LineNumberPrefix = new(@"^ *(\d{1,7})(\t|\u2192)", RegexOptions.CultureInvariant);

    /// <summary>
    /// 模型把 read_file 的行号前缀一并复制进文本时去掉前缀：每一行（末尾的空行除外）都以“行号 + 制表符”开头且行号逐行加一时才视为前缀。
    /// 不是这种形式时返回 null。
    /// </summary>
    internal static string? StripLineNumbers(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var result = new string[lines.Length];
        int? previous = null;
        for (var i = 0; i < lines.Length; i++)
        {
            if (i == lines.Length - 1 && lines[i].Length == 0 && previous is not null) { result[i] = ""; continue; }
            var match = LineNumberPrefix.Match(lines[i]);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var number) || (previous is { } p && number != p + 1)) return null;
            previous = number;
            result[i] = lines[i].Substring(match.Length);
        }
        return previous is null ? null : string.Join(text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n", result);
    }

    /// <summary>
    /// old_text 带了行号前缀而 new_text 的行号不连续（模型插入了新行或没有重新编号）时，逐行去掉形如“行号 + 制表符”的前缀，没有前缀的行保持不变，
    /// 避免把行号写进文件。只在 old_text 已确认带行号时使用。
    /// </summary>
    private static string StripLinePrefixes(string text) =>
        string.Join(text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n",
            text.Replace("\r\n", "\n").Split('\n').Select(line => LineNumberPrefix.Match(line) is { Success: true } match ? line.Substring(match.Length) : line));

    /// <summary>write_file 的内容看起来是带行号的 read_file 输出（三行以上、从第 1 行开始）时拒绝，避免把行号写进文件。</summary>
    private static string RejectLineNumbers(string content)
    {
        if (content.Count(c => c == '\n') >= 2 && StripLineNumbers(content) is not null && LineNumberPrefix.Match(content).Groups[1].Value == "1")
            throw new WorkspaceException("content starts with read_file line-number prefixes (\"1<TAB>...\"). Remove the line numbers and write only the file text.");
        return content;
    }

    /// <summary>
    /// 运行结束后释放只在运行中有用的缓存（已知内容、待取的差异）。检查点会在整个 VS 会话中保留本对象用于撤销，不应一直占用这些内存。
    /// 撤销与改动记录不受影响。
    /// </summary>
    public void ReleaseRunCaches()
    {
        wholeReads.Clear();
        lock (knownTexts)
        {
            knownTexts.Clear();
            knownChars = 0;
        }
        lastWriteDiff = null;
    }

    /// <summary>
    /// 对内容未变的文件再次整篇读取时只回复“未变化”（与 Claude Code 相同），节省重复的输入 token。只能在模型仍能看到此前全文时开启：
    /// 会省略旧工具结果的场合（OpenAI 兼容服务的发送裁剪、外部客户端如 Claude CLI 自行压缩上下文）必须关闭；上下文被压缩后调用 <see cref="ForgetReturnedReads"/>。
    /// </summary>
    public bool DeduplicateReads { get; set; }

    /// <summary>忘记已交给模型的全文（上下文被压缩为摘要后，之前读到的内容模型已看不到）。</summary>
    public void ForgetReturnedReads() => wholeReads.Clear();

    /// <summary>已记住内容的总字符数上限（约 32 MB 内存）；超过时清空，只是少了一项优化。</summary>
    private const long MaxKnownChars = 16_000_000;
    private long knownChars;

    /// <summary>记住模型最近看到的磁盘内容（供“读取后被改动”时给出差异）。条目过多或总量超过上限时清空，只是少了一项优化。</summary>
    private void Remember(string full, string hash, string text)
    {
        lock (knownTexts)
        {
            if (knownTexts.TryGetValue(full, out var previous)) knownChars -= previous.Text.Length;
            if (knownTexts.Count >= 256 || knownChars + text.Length > MaxKnownChars)
            {
                knownTexts.Clear();
                knownChars = 0;
            }
            knownTexts[full] = (hash, text);
            knownChars += text.Length;
        }
    }

    /// <summary>
    /// 文件在模型读取后被改动（用户编辑、保存时格式化或其他工具）：已知读取时的内容且差异不大时，返回差异并把当前版本登记为已读，
    /// 模型可直接按当前内容重做修改；否则要求重新读取。两种情况都不写入。
    /// </summary>
    private string ChangedSinceRead(string full, string expected, byte[] bytes)
    {
        const string reread = "File changed since you read it. Read it again before editing.";
        if (!knownTexts.TryGetValue(full, out var known) || known.Hash != expected) return reread;
        var current = codec.Decode(bytes).Text;
        var (diff, truncated) = ChangePreview.UnifiedDiff(known.Text, current, context: 2, maxLines: 80);
        if (truncated || diff.Length == 0) return reread;
        var hash = Hash(bytes);
        readVersions[full] = hash;
        if (partialReads.ContainsKey(full)) partialReads[full] = hash;
        Remember(full, hash, current);
        RunLog.Info(RunId, "changed_since_read", ("path", Compat.RelativeTo(Root, full)), ("diffChars", diff.Length));
        return "File changed since you read it (by the user, the editor or another tool); nothing was written. Changes since your read " +
            "(lines start with -, + or space; @@ -old +new @@ gives line numbers):\n" + diff +
            $"\nThe current version (sha256 {hash}) is now registered as read. Redo your edit against the current content; omit expected_sha256 or pass the new one. Keep the other person's changes.";
    }

    /// <summary>一处替换：<see cref="ReplaceAll"/> 为 true 时替换全部出现处，否则必须恰好出现一次。</summary>
    private sealed record EditSpec(string OldText, string NewText, bool ReplaceAll);

    /// <summary>
    /// 按顺序应用一处或多处替换，全部成功后一次写入（一次审批、一次备份）；任一处失败时不写入。
    /// 结果附带修改后区域的带行号片段，模型无需重新读取即可核对。
    /// </summary>
    private async Task<string> EditAsync(string path, IReadOnlyList<EditSpec> edits, string expected, CancellationToken token)
    {
        if (edits.Count is 0 or > 50) throw new WorkspaceException("Provide between 1 and 50 edits.");
        var full = policy.Resolve(path);
        var bytes = await ReadBytesAsync(full, token);
        if (Hash(bytes) != expected) throw new WorkspaceException(ChangedSinceRead(full, expected, bytes));
        var text = codec.Decode(bytes).Text;
        var applied = new List<string>();
        var notes = new List<string>();
        for (var i = 0; i < edits.Count; i++)
        {
            var edit = edits[i];
            var label = edits.Count > 1 ? $"Edit {i + 1}: " : "";
            if (edit.OldText.Length == 0) throw new WorkspaceException(label + "old_text must not be empty.");
            if (edit.OldText == edit.NewText) throw new WorkspaceException(label + "old_text and new_text are identical; nothing to change.");
            try
            {
                if (edit.ReplaceAll)
                {
                    var (oldText, newText) = (edit.OldText, edit.NewText);
                    if (Occurrences(text, oldText).Count == 0 && StripLineNumbers(oldText) is { } bare)
                    {
                        (oldText, newText) = (bare, StripLineNumbers(newText) ?? StripLinePrefixes(newText));
                        notes.Add(label + "Removed read_file line-number prefixes from old_text/new_text; do not include them.");
                    }
                    var (variant, positions) = LocateAll(text, oldText);
                    for (var k = positions.Count - 1; k >= 0; k--) text = text.Substring(0, positions[k]) + newText + text.Substring(positions[k] + variant.Length);
                    applied.Add(newText);
                }
                else
                {
                    var (first, length, newText, note) = LocateForEdit(text, edit.OldText, edit.NewText, stripped: false);
                    text = text.Substring(0, first) + newText + text.Substring(first + length);
                    applied.Add(newText);
                    if (note is not null) notes.Add(label + note);
                }
            }
            catch (WorkspaceException ex) { throw new WorkspaceException(label + ex.Message); }
        }
        var result = System.Text.Json.Nodes.JsonNode.Parse(await WriteAsync(path, text, expected, token))!.AsObject();
        if (edits.Count > 1) result["edits_applied"] = edits.Count;
        if (notes.Count > 0) result["match_notes"] = string.Join(" ", notes.Distinct());
        var snippets = applied.Select(t => Snippet(text, t)).Where(s => s is not null).Distinct().Take(5).ToList();
        if (snippets.Count > 0) result["updated_regions"] = string.Join("\n...\n", snippets);
        return result.ToJsonString();
    }

    /// <summary>
    /// replace_between：把从起始行到结束行（含两端）的整段替换为 <paramref name="newText"/>（为空时删除这几行）。起止行按“去掉首尾空白后整行相同”匹配：
    /// 起始行必须唯一，结束行取起始行之后（可为同一行）第一处匹配。用于“替换片段”式改动：只需给出起止两行原文。
    /// </summary>
    private async Task<string> ReplaceBetweenAsync(string path, string startLine, string endLine, string newText, string? within, string expected, CancellationToken token)
    {
        var full = policy.Resolve(path);
        // within：只在该成员（Roslyn 定位，必须恰好一个且在本文件中）的行范围内查找起止行，对应“两行在该成员内唯一”。
        (int From, int To)? scope = null;
        string? scopeText = null;
        if (!string.IsNullOrWhiteSpace(within))
        {
            if (ide is null) throw new WorkspaceException("Code navigation is not available in this host; omit within.");
            var declarations = (await ide.ReadSymbolAsync(within!.Trim(), 100_000, token))
                .Where(d => string.Equals(Path.GetFullPath(d.File), full, StringComparison.OrdinalIgnoreCase)).ToList();
            if (declarations.Count != 1)
                throw new WorkspaceException(declarations.Count == 0 ? $"{within} has no declaration in {path}. Check the name with find_symbol."
                    : $"{within} matches {declarations.Count} declarations in {path}; qualify it with parameter types.");
            scope = (declarations[0].StartLine - 1, declarations[0].EndLine - 1);
            scopeText = declarations[0].Text;
        }
        var bytes = await ReadBytesAsync(full, token);
        if (Hash(bytes) != expected) throw new WorkspaceException(ChangedSinceRead(full, expected, bytes));
        var text = codec.Decode(bytes).Text;
        var wantedStart = (StripLineNumbers(startLine) ?? startLine).Trim();
        var wantedEnd = (StripLineNumbers(endLine) ?? endLine).Trim();
        if (wantedStart.Length == 0 || wantedEnd.Length == 0 || wantedStart.IndexOf('\n') >= 0 || wantedEnd.IndexOf('\n') >= 0)
            throw new WorkspaceException("start_line and end_line must each be one nonempty line copied from the file.");
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        string Line(int index)
        {
            var end = index + 1 < starts.Count ? starts[index + 1] - 1 : text.Length;
            return text.Substring(starts[index], end - starts[index]).TrimEnd('\r');
        }
        var lineCount = text.EndsWith("\n", StringComparison.Ordinal) ? starts.Count - 1 : starts.Count;
        var (scopeFrom, scopeTo) = scope ?? (0, lineCount - 1);
        scopeTo = Math.Min(scopeTo, lineCount - 1);
        // 成员的行号来自 VS 中的代码：与磁盘内容不一致（未保存或未重新加载）时拒绝，避免在错位的范围内查找。
        if (scopeText is not null && string.Join("\n", Enumerable.Range(scopeFrom, Math.Max(0, scopeTo - scopeFrom + 1)).Select(Line)).TrimEnd() != scopeText.Replace("\r\n", "\n").TrimEnd())
            throw new WorkspaceException($"{within} in Visual Studio differs from the file on disk (unsaved changes or not yet reloaded). Save the file, or omit within.");
        var startHits = Enumerable.Range(scopeFrom, Math.Max(0, scopeTo - scopeFrom + 1)).Where(i => Line(i).Trim() == wantedStart).ToList();
        if (startHits.Count == 0) throw new WorkspaceException("start_line was not found as a whole line (ignoring leading and trailing whitespace)." + NotFoundHint(text, wantedStart));
        if (startHits.Count > 1)
            throw new WorkspaceException($"start_line occurs {startHits.Count} times (lines {string.Join(", ", startHits.Take(8).Select(h => h + 1))}). Use a more distinctive line, or edit_file.");
        var first = startHits[0];
        var last = Enumerable.Range(first, scopeTo - first + 1).Where(i => Line(i).Trim() == wantedEnd).Select(i => (int?)i).FirstOrDefault()
            ?? throw new WorkspaceException($"end_line was not found at or after line {first + 1} (the start line).");
        var from = starts[first];
        var to = last + 1 < starts.Count ? starts[last + 1] : text.Length;
        var newline = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
        // 被替换的最后一行有换行时新文本也以换行结束；只有替换到没有换行的文件末行时才不补。
        var block = newText.Length == 0 || newText.EndsWith("\n", StringComparison.Ordinal) || (to == text.Length && !text.EndsWith("\n", StringComparison.Ordinal)) ? newText : newText + newline;
        var updated = text.Substring(0, from) + block + text.Substring(to);
        var result = System.Text.Json.Nodes.JsonNode.Parse(await WriteAsync(path, updated, expected, token))!.AsObject();
        result["replaced_lines"] = $"{first + 1}-{last + 1}";
        if (newText.Trim().Length > 0 && Snippet(updated, newText.TrimEnd('\r', '\n')) is { } region) result["updated_regions"] = region;
        return result.ToJsonString();
    }

    /// <summary>
    /// 修改后区域的带行号片段（前后各 3 行，最多 40 行）；新文本为空或出现多次时返回 null。行号从 1 开始，与 read_file 的分段读取一致。
    /// </summary>
    internal static string? Snippet(string text, string newText)
    {
        if (newText.Trim().Length == 0) return null;
        var found = Occurrences(text, newText);
        if (found.Count != 1) return null;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var startLine = CountNewlines(text, found[0]);
        var endLine = CountNewlines(text, found[0] + newText.Length);
        var from = Math.Max(0, startLine - 3);
        var to = Math.Min(lines.Length - 1, Math.Min(endLine + 3, from + 39));
        var builder = new StringBuilder();
        for (var i = from; i <= to; i++) builder.Append(i + 1).Append('\t').Append(lines[i]).Append('\n');
        return builder.ToString().TrimEnd('\n');

        static int CountNewlines(string value, int end)
        {
            var count = 0;
            for (var i = 0; i < end && i < value.Length; i++) if (value[i] == '\n') count++;
            return count;
        }
    }

    /// <summary>修改用的版本：参数中的 expected_sha256；省略时使用本次运行中最近一次读取或写入该文件得到的版本。</summary>
    private string ExpectedVersion(JsonElement arguments, string path)
    {
        if (Optional(arguments, "expected_sha256") is { } given) return given;
        return readVersions.TryGetValue(policy.Resolve(path), out var version) ? version
            : throw new WorkspaceException("Read the file with read_file first (or pass expected_sha256).");
    }

    private static IReadOnlyList<EditSpec> Edits(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("edits", out var array) || array.ValueKind != JsonValueKind.Array)
            throw new WorkspaceException("edits must be an array of {old_text, new_text, replace_all?} objects.");
        var result = new List<EditSpec>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new WorkspaceException("Each edit must be an object with old_text and new_text.");
            result.Add(new(Required(item, "old_text"), Required(item, "new_text", true), OptionalBool(item, "replace_all") ?? false));
        }
        return result;
    }

    /// <summary>replace_all 的定位：先按原文，找不到时按文件的换行符再找；返回实际匹配的文本与全部位置。找不到时抛出带提示的错误。</summary>
    private static (string Variant, IReadOnlyList<int> Positions) LocateAll(string text, string oldText)
    {
        foreach (var variant in new[] { oldText, oldText.Replace("\r\n", "\n").Replace("\n", "\r\n"), oldText.Replace("\r\n", "\n") }.Distinct())
            if (Occurrences(text, variant) is { Count: > 0 } found) return (variant, found);
        throw new WorkspaceException("old_text was not found." + NotFoundHint(text, oldText));
    }

    /// <summary>
    /// 定位一处修改并给出实际要写入的新文本。依次尝试：精确匹配（含换行符与行尾空白容错，见 <see cref="LocateEdit"/>）；
    /// 去掉误带的 read_file 行号前缀；弯引号与直引号视为相同；整体缩进不同（模型多写或少写了一层统一的前导空白）时按同样的差值调整新文本。
    /// 后几种情况返回说明，告知模型实际如何匹配。都失败时抛出 <see cref="LocateEdit"/> 的错误（含提示）。
    /// </summary>
    private static (int Start, int Length, string NewText, string? Note) LocateForEdit(string text, string oldText, string newText, bool stripped)
    {
        if (!stripped && Occurrences(text, oldText).Count == 0 && StripLineNumbers(oldText) is { } bare)
        {
            var (start, length, adjusted, note) = LocateForEdit(text, bare, StripLineNumbers(newText) ?? StripLinePrefixes(newText), stripped: true);
            return (start, length, adjusted, "Removed read_file line-number prefixes from old_text/new_text; do not include them." + (note is null ? "" : " " + note));
        }
        try
        {
            var (start, length) = LocateEdit(text, oldText);
            return (start, length, newText, null);
        }
        catch (WorkspaceException) when (LocateNormalizingQuotes(text, oldText) is { } quoted)
        {
            return (quoted.Start, quoted.Length, newText, "old_text matched after treating curly and straight quotes as equal.");
        }
        catch (WorkspaceException) when (LocateIgnoringIndentation(text, oldText, newText) is { } shifted)
        {
            return (shifted.Start, shifted.Length, shifted.NewText,
                "old_text matched only after adjusting its overall indentation; new_text was re-indented by the same amount. Check updated_regions.");
        }
    }

    private static string StraightQuotes(string value) =>
        value.Replace('\u201C', '"').Replace('\u201D', '"').Replace('\u201E', '"').Replace('\u2018', '\'').Replace('\u2019', '\'');

    /// <summary>弯引号（“”„‘’）与直引号视为相同后唯一匹配（逐字符替换，位置不变）；任一方都没有弯引号或匹配不唯一时返回 null。</summary>
    private static (int Start, int Length)? LocateNormalizingQuotes(string text, string oldText)
    {
        var straightText = StraightQuotes(text);
        var lf = oldText.Replace("\r\n", "\n");
        foreach (var variant in new[] { oldText, lf.Replace("\n", "\r\n"), lf }.Distinct())
        {
            var straight = StraightQuotes(variant);
            if (straight == variant && straightText == text) return null;
            var found = Occurrences(straightText, straight);
            if (found.Count == 1) return (found[0], straight.Length);
            if (found.Count > 1) return null;
        }
        return null;
    }

    /// <summary>
    /// old_text 的每一行去掉首尾空白后与文件中连续的整行相同，且所有非空行的前导空白都相差同一个前缀（文件多一层，或 old_text 多一层），并且只有一处这样的位置。
    /// 返回文件中的区间以及按同样差值调整缩进后的新文本；不满足时返回 null。
    /// </summary>
    private static (int Start, int Length, string NewText)? LocateIgnoringIndentation(string text, string oldText, string newText)
    {
        var wanted = oldText.Replace("\r\n", "\n").Split('\n').ToList();
        var endsWithNewline = wanted.Count > 1 && wanted[wanted.Count - 1].Length == 0;
        if (endsWithNewline) wanted.RemoveAt(wanted.Count - 1);
        if (wanted.All(l => l.Trim().Length == 0)) return null;
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        string Line(int index)
        {
            var end = index + 1 < starts.Count ? starts[index + 1] - 1 : text.Length;
            var line = text.Substring(starts[index], end - starts[index]);
            return line.EndsWith("\r", StringComparison.Ordinal) ? line.Substring(0, line.Length - 1) : line;
        }
        static string Indent(string line) => line.Substring(0, line.Length - line.TrimStart().Length);
        var anchor = wanted.FindIndex(l => l.Trim().Length > 0);
        (int Line, string Delta, bool Add)? match = null;
        for (var i = 0; i + wanted.Count <= starts.Count; i++)
        {
            var same = true;
            for (var k = 0; k < wanted.Count && same; k++) same = Line(i + k).Trim() == wanted[k].Trim();
            if (!same) continue;
            string fileIndent = Indent(Line(i + anchor)), modelIndent = Indent(wanted[anchor]);
            bool add;
            string delta;
            if (fileIndent.Length > modelIndent.Length && fileIndent.EndsWith(modelIndent, StringComparison.Ordinal)) { add = true; delta = fileIndent.Substring(0, fileIndent.Length - modelIndent.Length); }
            else if (modelIndent.Length > fileIndent.Length && modelIndent.EndsWith(fileIndent, StringComparison.Ordinal)) { add = false; delta = modelIndent.Substring(0, modelIndent.Length - fileIndent.Length); }
            // 缩进相同的位置（精确或只差行尾空白的匹配）也算候选：old_text 本身有歧义时不能退而改动另一处缩进不同的代码。
            else if (fileIndent == modelIndent) { add = true; delta = ""; }
            else continue;
            var consistent = true;
            for (var k = 0; k < wanted.Count && consistent; k++)
            {
                if (wanted[k].Trim().Length == 0) continue;
                consistent = add ? Indent(Line(i + k)) == delta + Indent(wanted[k]) : Indent(wanted[k]) == delta + Indent(Line(i + k));
            }
            if (!consistent) continue;
            if (match is not null) return null;
            match = (i, delta, add);
        }
        if (match is not { } found) return null;
        var last = found.Line + wanted.Count - 1;
        var stop = endsWithNewline && last + 1 < starts.Count ? starts[last + 1] : starts[last] + Line(last).Length;
        var adjusted = string.Join("\n", newText.Replace("\r\n", "\n").Split('\n').Select(line =>
            line.Trim().Length == 0 ? line
            : found.Add ? found.Delta + line
            : line.StartsWith(found.Delta, StringComparison.Ordinal) ? line.Substring(found.Delta.Length) : line.TrimStart()));
        return (starts[found.Line], stop - starts[found.Line], adjusted);
    }

    /// <summary>
    /// 定位 old_text：先按原文精确匹配；找不到且含换行时，按文件的换行符（CRLF / LF）再匹配一次——模型回传多行文本时常丢掉 \r。
    /// 必须恰好匹配一处；找不到与多处匹配给出不同的错误，便于模型修正。
    /// </summary>
    internal static (int Start, int Length) LocateEdit(string text, string oldText)
    {
        var exact = Occurrences(text, oldText);
        if (exact.Count == 0 && oldText.IndexOf('\n') >= 0)
        {
            var lf = oldText.Replace("\r\n", "\n");
            foreach (var variant in new[] { lf.Replace("\n", "\r\n"), lf }.Where(v => v != oldText))
                if (Occurrences(text, variant) is { Count: > 0 } found)
                {
                    if (found.Count == 1) return (found[0], variant.Length);
                    exact = found;
                    break;
                }
        }
        if (exact.Count == 0 && LocateIgnoringTrailingWhitespace(text, oldText) is { } lines) return lines;
        return exact.Count switch
        {
            1 => (exact[0], oldText.Length),
            0 => throw new WorkspaceException("old_text was not found. Copy it exactly from the latest read_file content, including indentation, or read the file again." + NotFoundHint(text, oldText)),
            _ => throw new WorkspaceException($"old_text occurs {exact.Count} times (starting at line {string.Join(", ", exact.Take(8).Select(p => LineOf(text, p)))}" +
                (exact.Count > 8 ? ", ..." : "") + "). Include more surrounding lines so that it occurs exactly once, or set replace_all to change every occurrence."),
        };
    }

    /// <summary>
    /// 整行匹配且忽略每行行尾空白（模型复制时常丢失或多出行尾空格）：old_text 的每一行与文件中连续的整行相同（行尾空白除外），且只有一处。
    /// 返回文件中的实际区间；不满足时返回 null。
    /// </summary>
    private static (int Start, int Length)? LocateIgnoringTrailingWhitespace(string text, string oldText)
    {
        var wanted = oldText.Replace("\r\n", "\n").Split('\n').ToList();
        var endsWithNewline = wanted.Count > 1 && wanted[wanted.Count - 1].Length == 0;
        if (endsWithNewline) wanted.RemoveAt(wanted.Count - 1);
        if (wanted.Count == 0 || wanted.All(l => l.Trim().Length == 0)) return null;
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        string Line(int index)
        {
            var end = index + 1 < starts.Count ? starts[index + 1] - 1 : text.Length;
            var line = text.Substring(starts[index], end - starts[index]);
            return line.EndsWith("\r", StringComparison.Ordinal) ? line.Substring(0, line.Length - 1) : line;
        }
        int? match = null;
        for (var i = 0; i + wanted.Count <= starts.Count; i++)
        {
            var same = true;
            for (var k = 0; k < wanted.Count && same; k++) same = Line(i + k).TrimEnd() == wanted[k].TrimEnd();
            if (!same) continue;
            if (match is not null) return null;
            match = i;
        }
        if (match is not { } first) return null;
        var last = first + wanted.Count - 1;
        var stop = endsWithNewline && last + 1 < starts.Count ? starts[last + 1] : starts[last] + Line(last).Length;
        return (starts[first], stop - starts[first]);
    }

    /// <summary>位置所在的行号（从 1 开始）。</summary>
    private static int LineOf(string text, int position)
    {
        var line = 1;
        for (var i = 0; i < position && i < text.Length; i++) if (text[i] == '\n') line++;
        return line;
    }

    /// <summary>
    /// 找不到 old_text 时的提示：old_text 第一行（忽略缩进）在文件中出现的行号，并附上第一处的当前内容（带行号，最多 15 行），
    /// 模型可直接对照缩进与后续行修正，不必重新读取。
    /// </summary>
    private static string NotFoundHint(string text, string oldText)
    {
        var wanted = oldText.Replace("\r\n", "\n").Split('\n');
        var first = wanted.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (first is null) return "";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var hits = Enumerable.Range(0, lines.Length).Where(i => lines[i].Trim() == first).Take(5).ToList();
        if (hits.Count == 0) hits = Enumerable.Range(0, lines.Length).Where(i => lines[i].IndexOf(first, StringComparison.Ordinal) >= 0).Take(5).ToList();
        if (hits.Count == 0) return " Its first line does not appear in the file either; the code may have changed. Read the file again.";
        var from = hits[0];
        var to = Math.Min(lines.Length, from + Math.Min(15, wanted.Length + 2));
        var current = new StringBuilder();
        for (var i = from; i < to; i++) current.Append(i + 1).Append('\t').Append(lines[i].TrimEnd('\r')).Append('\n');
        return $" Its first line (ignoring indentation) appears at line {string.Join(", ", hits.Select(h => h + 1))}. The file currently has at line {from + 1}:\n{current.ToString().TrimEnd('\n')}\n" +
            "Compare indentation and the following lines, then retry with text copied from here (without the line-number prefixes).";
    }

    /// <summary>把文本的换行符统一为 <paramref name="fileText"/> 的主要换行符（CRLF 多于单独的 LF 时用 CRLF）。原文件没有换行时保持不变。</summary>
    internal static string MatchLineEndings(string newText, string fileText)
    {
        var crlf = Occurrences(fileText, "\r\n").Count;
        var lf = fileText.Count(c => c == '\n') - crlf;
        if (crlf == 0 && lf == 0) return newText;
        var normalized = newText.Replace("\r\n", "\n");
        return crlf > lf ? normalized.Replace("\n", "\r\n") : normalized;
    }

    /// <summary>文件中同时有 CRLF 与单独的 LF。</summary>
    internal static bool HasMixedLineEndings(string text)
    {
        var crlf = Occurrences(text, "\r\n").Count;
        return crlf > 0 && text.Count(c => c == '\n') > crlf;
    }

    private static List<int> Occurrences(string text, string value)
    {
        var result = new List<int>();
        if (value.Length == 0) return result;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            result.Add(index);
        return result;
    }

    private async Task<string> WriteAsync(string path, string content, string expected, CancellationToken token, bool wholeFile = false)
    {
        if (!AllowWrites) throw new WorkspaceException("This run is read-only. The user must enable writes before starting a new run.");
        var full = policy.Resolve(path);
        RequireWritable(full);
        if (content.IndexOf('\0') >= 0) throw new WorkspaceException("Output must be text without NUL characters.");
        await beforeWrite(full, token);
        policy.Resolve(path);
        token.ThrowIfCancellationRequested();
        var exists = File.Exists(full);
        if (exists && (!readVersions.TryGetValue(full, out var version) || version != expected))
            throw new WorkspaceException("Read the existing file first and use its latest SHA-256.");
        if (wholeFile && exists && partialReads.TryGetValue(full, out var partial) && partial == expected)
            throw new WorkspaceException("You have read only part of this file. Use edit_file for targeted changes, or read the whole file before using write_file.");
        if (!exists && expected != "new") throw new WorkspaceException("File is missing. Use expected_sha256='new' only to create a new file.");
        if (!exists && changes.TryGetValue(full, out var gone) && gone.AfterHash != Absent)
            throw new WorkspaceException("A previously changed file was removed externally. Start a new run after reviewing it.");
        if (!changes.ContainsKey(full) && changes.Count >= MaxChangedFiles) throw new WorkspaceException($"This run reached its {MaxChangedFiles}-file change limit.");
        // 审批前先按原文件编码试编码：无法表示的字符或超限在打扰用户之前就拒绝。
        var (before, format) = exists ? codec.Decode(await ReadBytesAsync(full, token)) : ("", TextCodec.NewFile);
        // 已有文件的换行符统一为其主要换行符（模型输出多为 LF）：不在 CRLF 文件中混入 LF，也修复先前已混杂的文件。
        var mixedBefore = exists && HasMixedLineEndings(before);
        if (exists) content = MatchLineEndings(content, before);
        if (TextCodec.Encode(content, format).Length > maxFileBytes) throw new WorkspaceException($"Output must be no larger than {maxFileBytes / 1024} KiB.");

        var userEdited = false;
        var relative = Compat.RelativeTo(Root, full).Replace('\\', '/');
        // 结果中附带增删行数，模型据此核对改动范围；用户在差异窗口中修改时按最终内容统计。
        string Finish(string json)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(userEdited ? MarkEdited(json) : json)!.AsObject();
            var (added, removed) = ChangePreview.CountLines(before, content);
            lastWriteDiff = relative + "\n" + ChangePreview.UnifiedDiff(before, content, context: 2, maxLines: 40).Text;
            node["lines_added"] = added;
            node["lines_removed"] = removed;
            return node.ToJsonString();
        }
        if (confirmWrite is not null && allowedForRun.Contains(full))
        {
            // 用户已在本次运行中允许此文件：不再请求批准；其余检查（版本、编辑器未保存状态、编码与大小）照常执行。
            RunLog.Info(RunId, "write_auto_approved", ("path", relative), ("newFile", !exists));
            WriteAutoApproved?.Invoke(relative);
        }
        else if (confirmWrite is not null)
        {
            var preview = ChangePreview.Create(relative, before, content, ApprovalPreviewChars) +
                (mixedBefore ? $"[原文件的换行符混杂（CRLF 与 LF）；本次写入统一为 {(content.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "CRLF" : "LF")}]\n" : "");
            var proposal = new WriteProposal(relative, !exists, preview, before, content);
            var approved = await confirmWrite(proposal, token);
            RunLog.Info(RunId, approved ? "write_approved" : "write_rejected", ("path", relative), ("newFile", !exists),
                ("userEdited", approved && proposal.Revised is { } r && r != content));
            if (!approved)
                throw new WorkspaceException("The user rejected this change. Do not repeat it; ask the user what they want instead.");
            if (proposal.AllowForRun && allowedForRun.Add(full))
                RunLog.Info(RunId, "write_allowed_for_run", ("path", relative));
            if (proposal.Revised is { } revised && revised != content)
            {
                // 用户在差异窗口中调整了改动：写入用户的版本，并按同样规则校验。
                if (revised.IndexOf('\0') >= 0) throw new WorkspaceException("The user's edited content contains NUL characters; nothing was written.");
                if (TextCodec.Encode(revised, format).Length > maxFileBytes) throw new WorkspaceException($"The user's edited content is larger than {maxFileBytes / 1024} KiB; nothing was written.");
                content = revised;
                userEdited = true;
            }
            // 审批期间用户可能编辑或打开了文件：重新检查编辑器状态与路径；版本由下方加锁后的哈希比较兜底。
            await beforeWrite(full, token);
            policy.Resolve(path);
            token.ThrowIfCancellationRequested();
            if (File.Exists(full) != exists) throw new WorkspaceException("File changed while waiting for approval. Read it again before writing.");
        }

        if (exists && ide is not null && await TryWriteThroughEditorAsync(full, path, content, expected, wholeFile, token) is { } viaEditor)
            return Finish(viaEditor);

        if (!exists)
        {
            // Journal a new-file intent before creating it, so backup failures cannot leave an untracked file.
            // 本次运行中删除过的文件保留原记录（含原始备份），撤销时恢复删除前的内容。
            if (changes.TryGetValue(full, out var deleted)) changes[full] = deleted with { AfterHash = null };
            else
            {
                Directory.CreateDirectory(BackupDirectory);
                var backup = Path.Combine(BackupDirectory, Guid.NewGuid().ToString("N") + ".before");
                await Compat.WriteAllBytesAsync(backup, [], token);
                changes[full] = new Change(Compat.RelativeTo(Root, full), false, backup, Hash([]), null);
            }
            await JournalAsync();
            policy.Resolve(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        }

        // Hold a handle denying other writers/deleters for the compare-and-write operation.
        // Cancellation is observed before mutation; once started, finish and journal this one file.
        using var file = new FileStream(full, exists ? FileMode.Open : FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.Read, 8192, useAsync: true);
        FileSafety.RejectHardLinks(file.SafeFileHandle);
        if (file.Length > maxFileBytes) throw new WorkspaceException($"File is larger than {maxFileBytes / 1024} KiB; it cannot be modified by the agent.");
        var original = new byte[(int)file.Length];
        await Compat.ReadExactlyAsync(file, original, CancellationToken.None);
        if (exists && Hash(original) != expected) throw new WorkspaceException("File changed. Read it again before writing.");
        var output = TextCodec.Encode(content, exists ? codec.Decode(original).Format : TextCodec.NewFile);
        if (!changes.TryGetValue(full, out var change))
        {
            Directory.CreateDirectory(BackupDirectory);
            var backup = Path.Combine(BackupDirectory, Guid.NewGuid().ToString("N") + ".before");
            await Compat.WriteAllBytesAsync(backup, original, CancellationToken.None);
            change = new Change(Compat.RelativeTo(Root, full), exists, backup, Hash(original), null);
            changes[full] = change;
        }
        // Persist intent before mutation; a failed write remains recoverable and visibly incomplete.
        changes[full] = change with { AfterHash = null };
        await JournalAsync();
        file.Position = 0;
        await file.WriteAsync(output, 0, output.Length, CancellationToken.None);
        file.SetLength(output.Length);
        await file.FlushAsync(CancellationToken.None);
        file.Flush(flushToDisk: true);
        changes[full] = change with { AfterHash = Hash(output) };
        RunLog.Info(RunId, "write", ("path", change.Path), ("via", "disk"), ("existed", change.Existed), ("before", change.BeforeHash.Substring(0, 12)),
            ("after", Hash(output).Substring(0, 12)), ("bytes", output.Length), ("userEdited", userEdited));
        readVersions[full] = Hash(output);
        KeepPartial(full, Hash(output), wholeFile);
        Remember(full, Hash(output), content);
        await JournalAsync();
        await RecordPreviewAsync(full, change, content);
        var result = JsonSerializer.Serialize(new { path, sha256 = Hash(output), written = true, bytes = output.Length });
        return Finish(result);
    }

    /// <summary>
    /// 写入保护：路径匹配 .slcopilot/settings.json 的保护规则（或该文件无法读取）时拒绝写入、删除与移动，自动批准时同样生效。
    /// 错误告诉模型不要换别的方式修改，并把这一项留给用户。
    /// </summary>
    private void RequireWritable(string full)
    {
        if (Protection.Check(Compat.RelativeTo(Root, full)) is not { } reason) return;
        RunLog.Warn(RunId, "write_protected", ("path", Compat.RelativeTo(Root, full).Replace('\\', '/')));
        throw new WorkspaceException(reason + " The agent must not change it in any way (including commands or other tools); report it as needing the user's action.");
    }

    /// <summary>read_symbol 结果中的文件路径（用于附加按路径生效的指令）。</summary>
    private static IEnumerable<string> PathsIn(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return [];
            return results.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
                .Select(r => r.GetProperty("path").GetString()!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (JsonException) { return []; }
    }

    /// <summary>
    /// 第一次读写匹配某条按路径生效的指令的文件时，把指令附在工具结果中（instructions 字段），每条指令每次运行只附一次。
    /// 并行的只读工具可能同时调用，记录已附加的指令时加锁。
    /// </summary>
    private ToolResult AttachScoped(ToolResult result, IEnumerable<string> paths)
    {
        if (result.IsError || scopedInstructions.Count == 0) return result;
        var fresh = new List<ScopedInstruction>();
        lock (appliedScoped)
            foreach (var path in paths)
                foreach (var instruction in scopedInstructions)
                    if (instruction.Matches(path) && appliedScoped.Add(instruction.RelativePath)) fresh.Add(instruction);
        if (fresh.Count == 0) return result;
        RunLog.Info(RunId, "scoped_instructions", ("files", string.Join(",", fresh.Select(f => f.RelativePath))));
        var array = new System.Text.Json.Nodes.JsonArray(fresh.Select(i => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
        {
            ["file"] = i.RelativePath, ["applies_to"] = string.Join(", ", i.ApplyTo), ["text"] = i.Text,
        }).ToArray());
        const string note = "Project rules (written by the project team) for files matching applies_to. Follow them for all work on matching files in this run.";
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(result.Content) is System.Text.Json.Nodes.JsonObject node)
            {
                node["instructions"] = array;
                node["instructions_note"] = note;
                return result with { Content = node.ToJsonString() };
            }
        }
        catch (JsonException) { }
        return result with { Content = result.Content + "\n\n[" + note + "]\n" + array.ToJsonString() };
    }

    /// <summary>
    /// 写入后的部分读取标记：write_file 写入整个文件，模型知道全部内容，清除标记；局部修改（edit_file、replace_symbol 等）保留写入前的状态并更新为新版本，
    /// 只读过一部分的文件之后仍不能用 write_file 整体覆盖。
    /// </summary>
    private void KeepPartial(string full, string newHash, bool wholeFile)
    {
        if (!wholeFile && partialReads.ContainsKey(full)) partialReads[full] = newHash;
        else partialReads.TryRemove(full, out _);
    }

    /// <summary>告知模型写入的是用户修改后的版本，后续修改前须重新读取。</summary>
    private static string MarkEdited(string result)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(result)!.AsObject();
        json["user_edited"] = true;
        json["note"] = "The user edited your proposed content before approving; the file contains the user's version. Read it again before further edits.";
        return json.ToJsonString();
    }

    /// <summary>
    /// 文件在编辑器中打开时经编辑器写入：VS 不弹“重新加载”，修改进入编辑器撤销栈。备份与意图记录先于修改落盘；
    /// 编辑器在修改前拒绝时恢复原记录。文件未打开或编码不一致时返回 null，由调用方按磁盘路径写入。
    /// </summary>
    private async Task<string?> TryWriteThroughEditorAsync(string full, string path, string content, string expected, bool wholeFile, CancellationToken token)
    {
        var original = await ReadBytesAsync(full, token);
        if (Hash(original) != expected) throw new WorkspaceException("File changed. Read it again before writing.");
        var (text, format) = codec.Decode(original);
        TextCodec.Encode(content, format);
        var hadChange = changes.TryGetValue(full, out var previous);
        var change = previous;
        if (change is null)
        {
            Directory.CreateDirectory(BackupDirectory);
            var backup = Path.Combine(BackupDirectory, Guid.NewGuid().ToString("N") + ".before");
            await Compat.WriteAllBytesAsync(backup, original, CancellationToken.None);
            change = new Change(Compat.RelativeTo(Root, full), true, backup, Hash(original), null);
        }
        changes[full] = change with { AfterHash = null };
        await JournalAsync();
        bool written;
        try { written = await ide!.TryWriteOpenDocumentAsync(full, text, content, format, token); }
        catch (Exception ex) when (ex is WorkspaceException or OperationCanceledException)
        {
            // 编辑器保证未修改：恢复原记录。
            await RestoreRecordAsync(full, hadChange, previous);
            throw;
        }
        if (!written)
        {
            await RestoreRecordAsync(full, hadChange, previous);
            return null;
        }
        var saved = await ReadBytesAsync(full, CancellationToken.None);
        var afterHash = Hash(saved);
        changes[full] = change with { AfterHash = afterHash };
        RunLog.Info(RunId, "write", ("path", change.Path), ("via", "editor"), ("existed", true), ("before", change.BeforeHash.Substring(0, 12)),
            ("after", afterHash.Substring(0, 12)), ("bytes", saved.Length));
        readVersions[full] = afterHash;
        KeepPartial(full, afterHash, wholeFile);
        if (saved.Length <= maxFileBytes) Remember(full, afterHash, codec.Decode(saved).Text);
        await JournalAsync();
        await RecordPreviewAsync(full, change, content);
        var exact = codec.Decode(saved).Text == content;
        return JsonSerializer.Serialize(new
        {
            path, sha256 = afterHash, written = true, via = "editor", bytes = saved.Length,
            warning = exact ? null : "The saved file differs from the requested content (for example a format-on-save extension). Read it again before further edits.",
        });
    }

    private async Task RestoreRecordAsync(string full, bool hadChange, Change? previous)
    {
        if (hadChange) changes[full] = previous!;
        else changes.Remove(full);
        await JournalAsync();
    }

    /// <summary>删除已读取的文件：用户批准后先备份并记账，再在禁止其他写入的句柄上比较版本并删除。撤销时按备份重新创建。</summary>
    private async Task<string> DeleteAsync(string path, string expected, CancellationToken token)
    {
        if (!AllowWrites) throw new WorkspaceException("This run is read-only. The user must enable writes before starting a new run.");
        var full = policy.Resolve(path);
        RequireWritable(full);
        await beforeWrite(full, token);
        policy.Resolve(path);
        token.ThrowIfCancellationRequested();
        if (!File.Exists(full)) throw new WorkspaceException("File does not exist.");
        if (!readVersions.TryGetValue(full, out var version) || version != expected) throw new WorkspaceException("Read the file first and use its latest SHA-256.");
        if (!changes.ContainsKey(full) && changes.Count >= MaxChangedFiles) throw new WorkspaceException($"This run reached its {MaxChangedFiles}-file change limit.");
        var bytes = await ReadBytesAsync(full, token);
        if (Hash(bytes) != expected) throw new WorkspaceException("File changed. Read it again before deleting.");
        var relative = Compat.RelativeTo(Root, full).Replace('\\', '/');
        var before = codec.Decode(bytes).Text;
        await ApproveAsync(new WriteProposal(relative, false, ChangePreview.Create(relative, before, "", ApprovalPreviewChars), before, "") { Kind = ProposalKind.Delete }, token);
        await beforeWrite(full, token);
        policy.Resolve(path);
        token.ThrowIfCancellationRequested();
        await DeleteCoreAsync(full, expected);
        return JsonSerializer.Serialize(new { path, deleted = true,
            note = "The file is deleted and backed up. Update project files and code that reference it if needed." });
    }

    /// <summary>
    /// 移动 / 重命名：用户批准后先建目标（CreateNew，内容逐字节相同）再删源，中途失败时最多多出一份副本，不会丢失内容。
    /// 两端各记一条变更（源为删除、目标为新建），撤销时分别恢复。
    /// </summary>
    private async Task<string> MoveAsync(string path, string newPath, string expected, CancellationToken token)
    {
        if (!AllowWrites) throw new WorkspaceException("This run is read-only. The user must enable writes before starting a new run.");
        var source = policy.Resolve(path);
        var target = policy.Resolve(newPath);
        RequireWritable(source);
        RequireWritable(target);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            throw new WorkspaceException("new_path must differ from path. Renames that only change letter case are not supported.");
        await beforeWrite(source, token);
        await beforeWrite(target, token);
        policy.Resolve(path);
        policy.Resolve(newPath);
        token.ThrowIfCancellationRequested();
        if (!File.Exists(source)) throw new WorkspaceException("File does not exist.");
        if (File.Exists(target) || Directory.Exists(target)) throw new WorkspaceException("new_path already exists. Choose another path, or delete that file first.");
        if (changes.TryGetValue(target, out var earlier) && earlier.AfterHash != Absent)
            throw new WorkspaceException("A previously changed file at new_path was removed externally. Start a new run after reviewing it.");
        if (!readVersions.TryGetValue(source, out var version) || version != expected) throw new WorkspaceException("Read the file first and use its latest SHA-256.");
        if (changes.Count + (changes.ContainsKey(source) ? 0 : 1) + (changes.ContainsKey(target) ? 0 : 1) > MaxChangedFiles)
            throw new WorkspaceException($"This run reached its {MaxChangedFiles}-file change limit.");
        var bytes = await ReadBytesAsync(source, token);
        if (Hash(bytes) != expected) throw new WorkspaceException("File changed. Read it again before moving.");
        var text = codec.Decode(bytes).Text;
        var from = Compat.RelativeTo(Root, source).Replace('\\', '/');
        var to = Compat.RelativeTo(Root, target).Replace('\\', '/');
        await ApproveAsync(new WriteProposal(from, false,
            $"移动 / 重命名：{from} → {to}\n内容不变（{bytes.Length:N0} 字节）。项目文件、命名空间与引用不会自动修改。\n", text, text)
            { Kind = ProposalKind.Move, Destination = to }, token);
        await beforeWrite(source, token);
        await beforeWrite(target, token);
        policy.Resolve(path);
        policy.Resolve(newPath);
        token.ThrowIfCancellationRequested();
        if (File.Exists(target) || Directory.Exists(target)) throw new WorkspaceException("new_path was created while waiting for approval. Nothing was moved.");

        await CreateCopyAsync(target, bytes, text);
        try { await DeleteCoreAsync(source, expected); }
        catch (WorkspaceException ex)
        {
            throw new WorkspaceException($"Created {newPath}, but {path} was not deleted: {ex.Message} Both files now exist; read them and decide with the user.");
        }
        readVersions[target] = Hash(bytes);
        RunLog.Info(RunId, "move", ("from", from), ("to", to), ("bytes", bytes.Length));
        return JsonSerializer.Serialize(new { path, new_path = newPath, moved = true, sha256 = Hash(bytes),
            note = "Content is unchanged. Update namespaces, project files and references yourself if needed." });
    }

    /// <summary>请求用户批准删除或移动；拒绝时抛出 <see cref="WorkspaceException"/>。不支持在差异窗口中编辑内容。</summary>
    private async Task ApproveAsync(WriteProposal proposal, CancellationToken token)
    {
        if (confirmWrite is null) return;
        var approved = await confirmWrite(proposal, token);
        RunLog.Info(RunId, approved ? "change_approved" : "change_rejected", ("kind", proposal.Kind.ToString()), ("path", proposal.RelativePath), ("to", proposal.Destination));
        if (!approved) throw new WorkspaceException("The user rejected this change. Do not repeat it; ask the user what they want instead.");
    }

    /// <summary>
    /// 在禁止其他进程写入的句柄上确认版本后删除。备份与意图记录先于删除落盘；删除调用失败时恢复原记录（文件未被删除）；
    /// 调用成功但文件仍存在时保留“未完成”记录并抛出 IOException。
    /// </summary>
    private async Task DeleteCoreAsync(string full, string expected)
    {
        var hadChange = changes.TryGetValue(full, out var previous);
        Change change;
        using (var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Delete, 8192, useAsync: true))
        {
            FileSafety.RejectHardLinks(file.SafeFileHandle);
            if (file.Length > maxFileBytes) throw new WorkspaceException($"File is larger than {maxFileBytes / 1024} KiB; it cannot be deleted by the agent.");
            var original = new byte[(int)file.Length];
            await Compat.ReadExactlyAsync(file, original, CancellationToken.None);
            if (Hash(original) != expected) throw new WorkspaceException("File changed. Read it again before deleting.");
            change = previous!;
            if (!hadChange)
            {
                Directory.CreateDirectory(BackupDirectory);
                var backup = Path.Combine(BackupDirectory, Guid.NewGuid().ToString("N") + ".before");
                await Compat.WriteAllBytesAsync(backup, original, CancellationToken.None);
                change = new Change(Compat.RelativeTo(Root, full), true, backup, Hash(original), null);
            }
            changes[full] = change with { AfterHash = null };
            await JournalAsync();
            try { File.Delete(full); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await RestoreRecordAsync(full, hadChange, previous);
                throw new WorkspaceException("The file could not be deleted (it may be read-only or in use). Nothing was changed.");
            }
        }
        if (File.Exists(full)) throw new WorkspaceException("The delete request succeeded but the file still exists; the change is marked incomplete. Tell the user to inspect the file and the backup.");
        changes[full] = change with { AfterHash = Absent };
        readVersions.TryRemove(full, out _);
        partialReads.TryRemove(full, out _);
        await JournalAsync();
        await RecordPreviewAsync(full, change, "");
        RunLog.Info(RunId, "delete", ("path", change.Path), ("before", change.BeforeHash.Substring(0, 12)), ("existed", change.Existed));
    }

    /// <summary>以 CreateNew 创建目标文件并写入 <paramref name="bytes"/>。意图先记账；文件未能创建时恢复原记录。</summary>
    private async Task CreateCopyAsync(string full, byte[] bytes, string text)
    {
        var hadChange = changes.TryGetValue(full, out var previous);
        var change = previous!;
        if (!hadChange)
        {
            Directory.CreateDirectory(BackupDirectory);
            var backup = Path.Combine(BackupDirectory, Guid.NewGuid().ToString("N") + ".before");
            await Compat.WriteAllBytesAsync(backup, [], CancellationToken.None);
            change = new Change(Compat.RelativeTo(Root, full), false, backup, Hash([]), null);
        }
        changes[full] = change with { AfterHash = null };
        await JournalAsync();
        FileStream file;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            file = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, useAsync: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await RestoreRecordAsync(full, hadChange, previous);
            throw new WorkspaceException("new_path could not be created (it may already exist or be read-only). Nothing was moved.");
        }
        using (file)
        {
            FileSafety.RejectHardLinks(file.SafeFileHandle);
            await file.WriteAsync(bytes, 0, bytes.Length, CancellationToken.None);
            await file.FlushAsync(CancellationToken.None);
            file.Flush(flushToDisk: true);
        }
        changes[full] = change with { AfterHash = Hash(bytes) };
        await JournalAsync();
        await RecordPreviewAsync(full, change, text);
    }

    /// <summary>编译整个解决方案，或只编译 <paramref name="project"/>（工作区内的项目文件，连同它引用的项目）。</summary>
    private async Task<string> BuildAsync(string? project, CancellationToken token)
    {
        if (!AllowWrites) throw new WorkspaceException("Building is only available in Agent mode. Use get_errors to read existing errors.");
        if (ide is null) throw new WorkspaceException("Building is not available in this host.");
        string? projectPath = null;
        if (project is not null)
        {
            projectPath = policy.Resolve(project);
            if (Path.GetExtension(projectPath).ToLowerInvariant() is not (".csproj" or ".vbproj" or ".fsproj" or ".vcxproj"))
                throw new WorkspaceException("project must be a project file such as src/App/App.csproj.");
            if (!File.Exists(projectPath)) throw new WorkspaceException("project does not exist.");
        }
        var outcome = await ide.BuildSolutionAsync(projectPath, token);
        var diagnostics = await ide.GetDiagnosticsAsync(100, token);
        return JsonSerializer.Serialize(new { succeeded = outcome.Succeeded, canceled = outcome.Canceled, diagnostics = MapDiagnostics(diagnostics) });
    }

    /// <summary>run_command：在工作区内的目录执行白名单程序。仅智能体模式；每条命令需用户批准。</summary>
    private async Task<string> CommandAsync(JsonElement arguments, CancellationToken token)
    {
        var runner = RequireCommands();
        var program = Required(arguments, "command");
        var args = OptionalStrings(arguments, "args");
        var cwd = Optional(arguments, "cwd") ?? ".";
        var directory = policy.Resolve(cwd, directory: true);
        if (!Directory.Exists(directory)) throw new WorkspaceException("cwd must be an existing directory in the workspace.");
        var timeout = OptionalInt(arguments, "timeout_seconds") ?? CommandRunner.DefaultTimeoutSeconds;
        var outcome = await runner.RunAsync(RunId, new(program, args, directory, Compat.RelativeTo(Root, directory).Replace('\\', '/'), timeout), Root, "执行命令", token);
        return JsonSerializer.Serialize(new
        {
            command = outcome.CommandLine, exit_code = outcome.ExitCode, succeeded = outcome.Succeeded, timed_out = outcome.TimedOut,
            still_running = outcome.StillRunning ? true : (bool?)null, duration_ms = outcome.DurationMs,
            stdout = outcome.StandardOutput, stdout_truncated = outcome.OutputTruncated ? true : (bool?)null,
            stderr = outcome.StandardError, stderr_truncated = outcome.ErrorTruncated ? true : (bool?)null,
            note = CommandNote(outcome),
        });
    }

    /// <summary>
    /// run_tests：执行 dotnet test（受同一白名单约束，需批准），结果写入工作区外的临时目录，读取 TRX 后删除。
    /// 没有读到任何测试结果时不报告成功：通常是编译失败、路径错误或筛选条件没有匹配到测试。
    /// </summary>
    private async Task<string> TestsAsync(JsonElement arguments, CancellationToken token)
    {
        var runner = RequireCommands();
        var args = new List<string> { "test" };
        if (Optional(arguments, "project") is { } project)
        {
            var isDirectory = Directory.Exists(Path.Combine(Root, project.Replace('/', Path.DirectorySeparatorChar)));
            var target = policy.Resolve(project, isDirectory);
            if (!isDirectory && Path.GetExtension(target).ToLowerInvariant() is not (".csproj" or ".vbproj" or ".fsproj" or ".sln" or ".slnx"))
                throw new WorkspaceException("project must be a test project, solution or directory in the workspace.");
            if (!isDirectory && !File.Exists(target)) throw new WorkspaceException("project does not exist.");
            // 相对工作区根目录（命令的工作目录）传递，不向模型暴露主机上的绝对路径。
            args.Add(Compat.RelativeTo(Root, target));
        }
        if (Optional(arguments, "filter") is { } filter)
        {
            if (filter.Length > 1000) throw new WorkspaceException("filter must be at most 1000 characters.");
            args.Add("--filter");
            args.Add(filter);
        }
        var results = Path.Combine(Path.GetTempPath(), "SLCopilot", "test-results", Guid.NewGuid().ToString("N"));
        args.AddRange(["--nologo", "--logger", "trx", "--results-directory", results]);
        var timeout = OptionalInt(arguments, "timeout_seconds") ?? DefaultTestTimeoutSeconds;
        try
        {
            var outcome = await runner.RunAsync(RunId, new("dotnet", args, Root, ".", timeout), Root, "运行测试", token);
            TestReport report;
            try { report = TestReportReader.Read(results, 30); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                RunLog.Warn(RunId, "test_report_failed", ("error", ex.GetType().Name), ("message", ex.Message));
                report = new(0, 0, 0, 0, [], 0);
            }
            var ran = report.Files > 0 && report.Total > 0;
            string Hide(string text) => text.Replace(results, "<test-results>");
            return JsonSerializer.Serialize(new
            {
                command = Hide(outcome.CommandLine), exit_code = outcome.ExitCode, timed_out = outcome.TimedOut,
                still_running = outcome.StillRunning ? true : (bool?)null, duration_ms = outcome.DurationMs,
                succeeded = outcome.Succeeded && ran && report.Failed == 0,
                total = report.Total, passed = report.Passed, failed = report.Failed, skipped = report.Skipped,
                failures = report.Failures.Select(f => new { name = f.Name, outcome = f.Outcome, message = f.Message, stack_trace = f.StackTrace }),
                output_tail = Tail(Hide(outcome.StandardOutput), 6000), stderr_tail = Tail(Hide(outcome.StandardError), 3000),
                note = CommandNote(outcome) ?? (!ran
                    ? "No test results were produced. The build may have failed, the project may contain no tests, or the filter matched nothing; see output_tail. Do not report the tests as passed."
                    : null),
            });
        }
        finally
        {
            try { if (Directory.Exists(results)) Directory.Delete(results, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RunLog.Warn(RunId, "test_results_cleanup_failed", ("message", ex.Message));
            }
        }
    }

    private CommandRunner RequireCommands()
    {
        if (!AllowWrites) throw new WorkspaceException("Commands and tests run only in Agent mode because they can change files. In this read-only run, tell the user what to run instead.");
        return commands ?? throw new WorkspaceException("Command execution is not available in this host.");
    }

    private static string? CommandNote(CommandOutcome outcome) =>
        outcome.StillRunning ? "The process did not exit after it was terminated. Tell the user to check it in Task Manager; do not start the command again."
        : outcome.TimedOut ? "The command exceeded its timeout and was terminated together with its child processes. Its work may be incomplete."
        : null;

    private static string Tail(string text, int max) => text.Length <= max ? text : "[...]\n" + text.Substring(text.Length - max);

    /// <summary>错误列表：可只看某个文件（<paramref name="path"/>）或只看错误；筛选前最多读取 1000 项，返回最多 100 项。</summary>
    private async Task<string> ErrorsAsync(string? path, bool errorsOnly, CancellationToken token)
    {
        if (ide is null) throw new WorkspaceException("The Error List is not available in this host.");
        var filtered = path is not null || errorsOnly;
        var diagnostics = await ide.GetDiagnosticsAsync(filtered ? 1000 : 100, token);
        IEnumerable<IdeDiagnostic> selected = diagnostics;
        if (errorsOnly) selected = selected.Where(d => d.Severity == "error");
        if (path is not null)
        {
            var target = policy.Resolve(path);
            selected = selected.Where(d =>
            {
                try { return !string.IsNullOrEmpty(d.File) && string.Equals(Path.GetFullPath(d.File), target, StringComparison.OrdinalIgnoreCase); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
            });
        }
        var list = selected.Take(100).ToList();
        return JsonSerializer.Serialize(new { diagnostics = MapDiagnostics(list), truncated = list.Count >= 100 || (!filtered && diagnostics.Count >= 100) });
    }

    private async Task<string> SymbolsAsync(string tool, string symbolName, CancellationToken token)
    {
        if (ide is null) throw new WorkspaceException("Code navigation is not available in this host.");
        if (symbolName.Length > 300) throw new WorkspaceException("Symbol name must be at most 300 characters.");
        var limit = tool == "find_symbol" ? 50 : 300;
        var found = tool == "find_symbol"
            ? await ide.FindSymbolsAsync(symbolName.Trim(), limit, token)
            : await ide.FindReferencesAsync(symbolName.Trim(), limit, token);
        var items = MapSymbols(found).ToList();
        return JsonSerializer.Serialize(new { results = items, truncated = found.Count >= limit,
            note = items.Count == 0 ? "No C#/VB matches. The project may not be loaded yet; fall back to search_files."
                : found.Any(s => s.Kind.StartsWith("suggestion:", StringComparison.Ordinal)) ? "No declaration has exactly this name; these are similar names. Retry with the exact name." : null });
    }

    /// <summary>继承与实现关系：类型的基类、接口、派生 / 实现类型；成员的重写与实现。框架中的符号没有文件，file 为空。</summary>
    private async Task<string> HierarchyAsync(string symbolName, CancellationToken token)
    {
        if (ide is null) throw new WorkspaceException("Code navigation is not available in this host.");
        if (symbolName.Length > 300) throw new WorkspaceException("Symbol name must be at most 300 characters.");
        var found = await ide.GetHierarchyAsync(symbolName.Trim(), 200, token);
        var items = MapSymbols(found).ToList();
        return JsonSerializer.Serialize(new
        {
            results = items, truncated = found.Count >= 200,
            note = items.Count == 0 ? "No inheritance or implementation relationships found. Check the name with find_symbol; the project may not be loaded yet." : null,
        });
    }

    /// <summary>
    /// 声明的源代码（每项最多 400 行）。只返回工作区内、工作区策略允许访问的文件；同时登记该文件的当前版本（视为部分读取），
    /// 之后可直接用 edit_file / multi_edit 修改（write_file 整体覆盖仍需先完整读取）。
    /// </summary>
    private async Task<string> ReadSymbolAsync(string symbolName, CancellationToken token)
    {
        if (ide is null) throw new WorkspaceException("Code navigation is not available in this host.");
        if (symbolName.Length > 300) throw new WorkspaceException("Symbol name must be at most 300 characters.");
        var found = await ide.ReadSymbolAsync(symbolName.Trim(), 400, token);
        var results = new List<object>();
        var skipped = 0;
        foreach (var item in found)
        {
            string full, relative;
            try
            {
                full = Path.GetFullPath(item.File);
                relative = Compat.RelativeTo(Root, full).Replace('\\', '/');
                policy.Resolve(relative);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { skipped++; continue; }
            string? hash = null;
            try
            {
                hash = Hash(await ReadBytesAsync(full, token));
                // 只在模型还没有该文件的版本时登记（按部分读取）。已登记旧版本而文件已变化时保留旧版本：之后的修改会得到“读取后被改动”的差异，
                // 而不是悄悄按模型过时的认识写入。
                if (!readVersions.TryGetValue(full, out var known))
                {
                    readVersions[full] = hash;
                    partialReads[full] = hash;
                }
                // 返回模型已登记的版本：按它修改时会得到“读取后被改动”的差异，而不是因版本不符被要求重新读取。
                else hash = known;
            }
            catch (IOException) { }
            results.Add(new { kind = item.Kind, name = item.Name, path = relative, start_line = item.StartLine, end_line = item.EndLine, sha256 = hash, content = item.Text });
        }
        return JsonSerializer.Serialize(new
        {
            results,
            note = results.Count == 0
                ? skipped > 0 ? "The declarations are outside the workspace or excluded by its rules." : "No C#/VB declaration with this name. Check the name with find_symbol."
                : skipped > 0 ? $"{skipped} declaration(s) outside the workspace or excluded by its rules were omitted." : null,
        });
    }

    /// <summary>get_callers / get_callees / get_type_dependencies：调用方、成员体内调用的成员、类型依赖。路径规则与 find_references 相同。</summary>
    private async Task<string> RelationsAsync(string tool, string symbolName, CancellationToken token)
    {
        if (ide is null) throw new WorkspaceException("Code navigation is not available in this host.");
        if (symbolName.Length > 300) throw new WorkspaceException("Symbol name must be at most 300 characters.");
        var found = tool switch
        {
            "get_callers" => await ide.GetCallersAsync(symbolName.Trim(), 200, token),
            "get_callees" => await ide.GetCalleesAsync(symbolName.Trim(), 200, token),
            _ => await ide.GetTypeDependenciesAsync(symbolName.Trim(), 200, token),
        };
        var items = MapSymbols(found).ToList();
        return JsonSerializer.Serialize(new
        {
            results = items, truncated = found.Count >= 200,
            note = items.Count == 0 ? "Nothing found. Check the name with find_symbol (get_callees needs a method, property or event; get_type_dependencies needs a type)." : null,
        });
    }

    /// <summary>解决方案的项目与项目引用；项目文件在工作区内时为相对路径，否则只保留文件名。</summary>
    private async Task<string> ProjectGraphAsync(CancellationToken token)
    {
        if (ide is null) throw new WorkspaceException("Code navigation is not available in this host.");
        var projects = await ide.GetProjectGraphAsync(token);
        return JsonSerializer.Serialize(new
        {
            projects = projects.Take(300).Select(p => new
            {
                name = p.Name, language = p.Language, assembly = p.AssemblyName, output = p.OutputKind, file = DisplayFile(p.File), documents = p.Documents, references = p.References,
            }),
            truncated = projects.Count > 300,
            note = projects.Count == 0 ? "No projects are loaded in Visual Studio's Roslyn workspace yet." : null,
        });
    }

    /// <summary>工作区内的文件为相对路径，工作区外只保留文件名，空路径保持为空。</summary>
    private string DisplayFile(string file)
    {
        if (string.IsNullOrEmpty(file)) return "";
        try
        {
            var full = Path.GetFullPath(file);
            return full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? Compat.RelativeTo(Root, full).Replace('\\', '/') : Path.GetFileName(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
    }

    private async Task<string> OutlineAsync(string path, CancellationToken token)
    {
        if (ide is null) throw new WorkspaceException("Code navigation is not available in this host.");
        var full = policy.Resolve(path);
        var outline = await ide.GetOutlineAsync(full, token);
        return JsonSerializer.Serialize(new
        {
            path, symbols = outline.Take(300).Select(s => new { kind = s.Kind, name = s.Name, line = s.Line, depth = s.Depth, signature = s.Text }),
            note = outline.Count == 0 ? "No C#/VB symbols. The file may not belong to a loaded C#/VB project; use read_file." : null,
        });
    }

    /// <summary>工作区内且未被忽略规则排除的位置转为相对路径；工作区外只保留文件名；被排除的文件不返回。</summary>
    private IEnumerable<object> MapSymbols(IReadOnlyList<CodeSymbol> symbols)
    {
        foreach (var s in symbols)
        {
            string file;
            if (string.IsNullOrEmpty(s.File))
            {
                // 元数据（框架、NuGet 包）中的符号：没有源文件。
                yield return new { kind = s.Kind, name = s.Name, file = "", line = 0, text = s.Text.Length > 300 ? s.Text.Substring(0, 300) + "…" : s.Text };
                continue;
            }
            try
            {
                var full = Path.GetFullPath(s.File);
                if (full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    file = Compat.RelativeTo(Root, full).Replace('\\', '/');
                    try { policy.Resolve(file); }
                    catch (IOException) { continue; }
                }
                else file = Path.GetFileName(full);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            var text = s.Text.Length > 300 ? s.Text.Substring(0, 300) + "…" : s.Text;
            yield return new { kind = s.Kind, name = s.Name, file, line = s.Line, text };
        }
    }

    /// <summary>工作区内的文件转为相对路径；工作区外只保留文件名，不向模型暴露其他目录结构。</summary>
    private IEnumerable<object> MapDiagnostics(IReadOnlyList<IdeDiagnostic> diagnostics) => diagnostics.Select(d =>
    {
        var file = "";
        if (!string.IsNullOrEmpty(d.File))
        {
            try
            {
                var full = Path.GetFullPath(d.File);
                file = full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    ? Compat.RelativeTo(Root, full).Replace('\\', '/') : Path.GetFileName(full);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { file = ""; }
        }
        var message = d.Message.Length > 500 ? d.Message.Substring(0, 500) + "…" : d.Message;
        return (object)new { severity = d.Severity, file, line = d.Line, column = d.Column, project = d.Project, message };
    });

    public string DescribeChanges() => changes.Count == 0 ? "No files changed." :
        string.Join("\n", changes.Values.Select(c => $"{Label(c)}: {c.Path}{(c.AfterHash is null ? " (write incomplete; inspect backup)" : "")}")) +
        "\n\nBackups: " + BackupDirectory;

    private static string Label(Change change) => change.AfterHash == Absent
        ? change.Existed ? "Deleted" : "Created and deleted"
        : change.Existed ? "Modified" : "Created";

    /// <summary>
    /// 本次运行改动的文件快照（按首次改动顺序）。改动记录只在执行工具的线程上修改：运行期间只能在该线程上读取
    /// （AgentRunner 随 changed_files 事件发送），运行结束后可在任意线程读取。
    /// </summary>
    public IReadOnlyList<ChangedFile> ChangedFiles => changes.Select(pair =>
    {
        var change = pair.Value;
        var kind = change.AfterHash == Absent ? change.Existed ? "deleted" : "created_and_deleted" : change.Existed ? "modified" : "created";
        var (added, removed) = lineCounts.TryGetValue(pair.Key, out var counts) ? counts : (0, 0);
        return new ChangedFile(change.Path.Replace('\\', '/'), pair.Key, change.Backup, kind, added, removed, change.AfterHash is not null);
    }).ToList();

    /// <summary>记录审阅预览与行数统计：修改前取自备份（按本次运行的编码解码），修改后为写入的文本。</summary>
    private async Task RecordPreviewAsync(string full, Change change, string after)
    {
        var before = codec.Decode(await ReadBytesAsync(change.Backup, CancellationToken.None)).Text;
        previews[full] = ChangePreview.Create(change.Path, before, after);
        lineCounts[full] = ChangePreview.CountLines(before, after);
    }

    public string DescribeDiffs()
    {
        var all = string.Join("\n", previews.Values);
        return all.Length > 60000 ? all[..60000] + "\n[More changes omitted; inspect files and backups]" : all;
    }

    /// <summary>
    /// 只恢复一个文件（工作区相对路径）到本次运行修改之前，规则与 <see cref="UndoAsync"/> 相同：文件在写入后被改动过时拒绝。
    /// 移动产生的两条记录需分别恢复。该文件没有改动记录时抛出 <see cref="WorkspaceException"/>。
    /// </summary>
    public async Task UndoFileAsync(string relativePath, CancellationToken token)
    {
        var full = policy.Resolve(relativePath);
        if (!changes.ContainsKey(full)) throw new WorkspaceException("这个文件没有本次运行的改动记录（可能已恢复）。");
        RunLog.Info(RunId, "undo_file_start", ("path", relativePath));
        try { await UndoCoreAsync(token, full); }
        catch (Exception ex)
        {
            RunLog.Error(RunId, "undo_file_failed", ex, ("path", relativePath));
            throw;
        }
    }

    public async Task UndoAsync(CancellationToken token)
    {
        RunLog.Info(RunId, "undo_start", ("files", changes.Count));
        try
        {
            await UndoCoreAsync(token);
            RunLog.Info(RunId, "undo_end");
        }
        catch (Exception ex)
        {
            RunLog.Error(RunId, "undo_failed", ex, ("remaining", changes.Count));
            throw;
        }
    }

    /// <param name="only">只恢复这个完整路径；为 null 时恢复全部。</param>
    private async Task UndoCoreAsync(CancellationToken token, string? only = null)
    {
        bool Selected(string key) => only is null || string.Equals(key, only, StringComparison.OrdinalIgnoreCase);
        // Preflight every file before restoring anything. Undo refuses changes made after the agent wrote.
        foreach (var change in changes.Where(p => Selected(p.Key)).Select(p => p.Value).ToList())
        {
            var full = policy.Resolve(change.Path);
            await beforeWrite(full, token);
            if (change.AfterHash == Absent)
            {
                if (File.Exists(full) || Directory.Exists(full))
                    throw new WorkspaceException("Cannot undo: a file the agent deleted has been recreated. Backups remain available.");
            }
            else if (change.AfterHash is null || Hash(await ReadBytesAsync(full, token)) != change.AfterHash)
                throw new WorkspaceException("Cannot undo: a file changed after the agent wrote it, or a write was incomplete. Backups remain available.");
            if (Hash(await ReadBytesAsync(change.Backup, token)) != change.BeforeHash)
                throw new WorkspaceException("Backup integrity check failed; nothing was restored.");
        }
        foreach (var pair in changes.Where(p => Selected(p.Key)).ToArray())
        {
            token.ThrowIfCancellationRequested();
            var change = pair.Value;
            var full = policy.Resolve(change.Path);
            await beforeWrite(full, token);
            var original = await Compat.ReadAllBytesAsync(change.Backup, token);
            if (Hash(original) != change.BeforeHash) throw new WorkspaceException("Backup integrity check failed.");
            if (change.AfterHash == Absent)
            {
                // 智能体删除的文件：原本存在则按备份重新创建（CreateNew，不覆盖任何新出现的文件）；本次新建后又删除的无需处理。
                if (change.Existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    using var restored = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true);
                    await restored.WriteAsync(original, 0, original.Length, CancellationToken.None);
                    await restored.FlushAsync(CancellationToken.None);
                    restored.Flush(flushToDisk: true);
                }
                RunLog.Info(RunId, "undo_file", ("path", change.Path), ("via", "disk"), ("recreated", change.Existed));
                changes.Remove(pair.Key);
                previews.Remove(pair.Key);
                lineCounts.Remove(pair.Key);
                await JournalAsync();
                continue;
            }
            if (change.Existed && ide is not null && await TryUndoThroughEditorAsync(full, original, change, token))
            {
                RunLog.Info(RunId, "undo_file", ("path", change.Path), ("via", "editor"));
                changes.Remove(pair.Key);
                readVersions.TryRemove(pair.Key, out _);
                previews.Remove(pair.Key);
                lineCounts.Remove(pair.Key);
                await JournalAsync();
                continue;
            }
            using (var file = new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 8192, true))
            {
                FileSafety.RejectHardLinks(file.SafeFileHandle);
                if (file.Length > maxFileBytes) throw new WorkspaceException("File changed after preflight.");
                var current = new byte[(int)file.Length];
                await Compat.ReadExactlyAsync(file, current, CancellationToken.None);
                if (Hash(current) != change.AfterHash) throw new WorkspaceException("File changed after preflight.");
                if (change.Existed)
                {
                    file.Position = 0;
                    await file.WriteAsync(original, 0, original.Length, CancellationToken.None);
                    file.SetLength(original.Length);
                    await file.FlushAsync(CancellationToken.None);
                }
            }
            if (!change.Existed)
            {
                policy.Resolve(change.Path);
                if (Hash(await ReadBytesAsync(full, token)) != change.AfterHash) throw new WorkspaceException("New file changed; not removed.");
                File.Delete(full);
            }
            RunLog.Info(RunId, "undo_file", ("path", change.Path), ("via", "disk"), ("deleted", !change.Existed));
            changes.Remove(pair.Key);
            readVersions.TryRemove(pair.Key, out _);
            previews.Remove(pair.Key);
            lineCounts.Remove(pair.Key);
            await JournalAsync();
        }
    }

    /// <summary>文件在编辑器中打开时经编辑器恢复原内容并保存；未打开或编码不一致时返回 false，由调用方写磁盘。</summary>
    private async Task<bool> TryUndoThroughEditorAsync(string full, byte[] original, Change change, CancellationToken token)
    {
        var current = await ReadBytesAsync(full, token);
        if (Hash(current) != change.AfterHash) throw new WorkspaceException("File changed after preflight.");
        var (beforeText, beforeFormat) = codec.Decode(original);
        if (!await ide!.TryWriteOpenDocumentAsync(full, codec.Decode(current).Text, beforeText, beforeFormat, token)) return false;
        if (Hash(await ReadBytesAsync(full, CancellationToken.None)) != change.BeforeHash)
            throw new WorkspaceException("The editor saved the restored text but the file differs from the backup (for example a format-on-save extension). Compare it with the backup: " + change.Backup);
        return true;
    }

    private async Task JournalAsync()
    {
        var path = Path.Combine(BackupDirectory, "manifest.json");
        var temp = path + ".tmp";
        await Compat.WriteAllBytesAsync(temp, new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(new { root = Root, changes = changes.Values },
            new JsonSerializerOptions { WriteIndented = true })), CancellationToken.None);
        Compat.MoveOverwrite(temp, path);
    }

    private async Task<byte[]> ReadBytesAsync(string path, CancellationToken token, long? limit = null)
    {
        var max = limit ?? maxFileBytes;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        FileSafety.RejectHardLinks(file.SafeFileHandle);
        if (file.Length > max) throw new WorkspaceException($"File is larger than {max / 1024} KiB. Narrow the task to smaller files.");
        var bytes = new byte[(int)file.Length];
        await Compat.ReadExactlyAsync(file, bytes, token);
        return bytes;
    }

    private static string Hash(byte[] bytes) => Compat.Sha256Hex(bytes);
    private static int CountLines(string text)
    {
        var count = 1;
        foreach (var c in text) if (c == '\n') count++;
        return text.EndsWith("\n", StringComparison.Ordinal) ? count - 1 : count;
    }

    private static int? OptionalInt(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)) return number;
        throw new WorkspaceException($"{name} must be an integer.");
    }

    private static bool? OptionalBool(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed)) return parsed;
        throw new WorkspaceException($"{name} must be true or false.");
    }

    /// <summary>可选的非空字符串参数；缺失、null 或空白时返回 null。</summary>
    private static string? Optional(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new WorkspaceException($"{name} must be a string.");
        return string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString();
    }

    private static IReadOnlyList<string> OptionalStrings(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return [];
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
            throw new WorkspaceException($"{name} must be an array of strings, one argument per element.");
        return value.EnumerateArray().Select(v => v.GetString()!).ToList();
    }

    private static string Required(JsonElement args, string name, bool allowEmpty = false)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            (!allowEmpty && string.IsNullOrWhiteSpace(value.GetString()))) throw new WorkspaceException($"Missing string argument: {name}");
        return value.GetString()!;
    }
    private sealed record Change(string Path, bool Existed, string Backup, string BeforeHash, string? AfterHash);
}

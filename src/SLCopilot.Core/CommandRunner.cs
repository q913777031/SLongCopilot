using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SLCopilot.Core;

/// <summary>命令白名单中的一条规则：程序名（不含路径与 .exe），可选限定第一个参数（子命令，区分大小写）。</summary>
public sealed record CommandRule(string Program, string? Subcommand)
{
    public override string ToString() => Subcommand is null ? Program : Program + " " + Subcommand;
}

/// <summary>
/// 智能体可执行命令的白名单。每行一条：“程序” 允许该程序的任意参数；“程序 子命令” 只允许第一个参数等于该子命令。
/// 空行与 # 开头的行忽略。程序名只能是名称（不含路径），只接受 .exe：.cmd / .bat 经 cmd.exe 解释，会重新解析参数。
/// </summary>
public static class CommandAllowlist
{
    /// <summary>默认白名单：编译、测试、还原与只读 git 查询。</summary>
    public static readonly string[] Defaults = ["dotnet build", "dotnet test", "dotnet restore", "git status", "git diff", "git log", "git show", "git blame"];
    public const int MaxRules = 50;
    private static readonly Regex ProgramName = new("^[A-Za-z0-9][A-Za-z0-9_+-]{0,63}(\\.exe)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SubcommandName = new("^[A-Za-z0-9-][A-Za-z0-9._:=-]{0,63}$", RegexOptions.CultureInvariant);

    /// <summary>解析规则；格式错误或超过 <see cref="MaxRules"/> 条时抛出 <see cref="ArgumentException"/>（消息含出错的行）。</summary>
    public static IReadOnlyList<CommandRule> Parse(IEnumerable<string>? lines)
    {
        var rules = new List<CommandRule>();
        foreach (var raw in lines ?? [])
        {
            var line = (raw ?? "").Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 2 || !ProgramName.IsMatch(parts[0]) || (parts.Length == 2 && !SubcommandName.IsMatch(parts[1])))
                throw new ArgumentException($"Invalid command rule \"{line}\". Use \"program\" or \"program subcommand\", for example \"dotnet test\".");
            var rule = new CommandRule(NormalizeProgram(parts[0]), parts.Length == 2 ? parts[1] : null);
            if (!rules.Contains(rule)) rules.Add(rule);
        }
        if (rules.Count > MaxRules) throw new ArgumentException($"Keep at most {MaxRules} command rules.");
        return rules;
    }

    /// <summary>程序名与某条规则一致（不区分大小写，忽略 .exe），且规则限定子命令时第一个参数与之完全相同。</summary>
    public static bool Allows(IReadOnlyList<CommandRule> rules, string program, IReadOnlyList<string> arguments)
    {
        var name = NormalizeProgram(program);
        return rules.Any(r => string.Equals(r.Program, name, StringComparison.OrdinalIgnoreCase) &&
            (r.Subcommand is null || (arguments.Count > 0 && string.Equals(arguments[0], r.Subcommand, StringComparison.Ordinal))));
    }

    internal static bool IsValidProgram(string program) => ProgramName.IsMatch(program);

    internal static string NormalizeProgram(string program) =>
        program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? program.Substring(0, program.Length - 4) : program;
}

/// <summary>一次命令请求。<see cref="WorkingDirectory"/> 为完整路径，<see cref="DisplayDirectory"/> 为展示给用户的工作区相对路径。</summary>
public sealed record CommandRequest(string Program, IReadOnlyList<string> Arguments, string WorkingDirectory, string DisplayDirectory, int TimeoutSeconds);

/// <summary>
/// 命令结果。<see cref="ExitCode"/> 为 null 表示超时或进程未确认退出；<see cref="StillRunning"/> 为 true 表示结束进程树后仍未确认退出。
/// 输出过长时保留开头与结尾，<see cref="OutputTruncated"/> / <see cref="ErrorTruncated"/> 为 true。
/// </summary>
public sealed record CommandOutcome(string CommandLine, int? ExitCode, bool TimedOut, bool StillRunning, long DurationMs,
    string StandardOutput, bool OutputTruncated, string StandardError, bool ErrorTruncated)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut && !StillRunning;
}

/// <summary>
/// 执行白名单内的程序：不经过 shell（无管道、重定向、通配符与变量展开），参数逐个传递；每条命令先经用户批准。
/// 进程放入 Windows 作业对象：超时、取消或结束后，它启动的整个进程树都会被结束。输入流立即关闭，交互式提示读到文件结尾。
/// 子进程不继承服务商密钥所在的环境变量。白名单不允许、程序找不到、用户拒绝或无法启动时抛出 <see cref="WorkspaceException"/>；
/// 取消时结束进程树后抛出 OperationCanceledException。实例可在多个运行间共用，但同一时间只应执行一条命令。
/// </summary>
public sealed class CommandRunner
{
    public const int DefaultTimeoutSeconds = 300;
    public const int MaxTimeoutSeconds = 1800;
    private const int MaxArguments = 64, MaxArgumentChars = 2000, MaxCommandLineChars = 8000;
    private const int HeadBytes = 8 * 1024, TailBytes = 24 * 1024;
    /// <summary>请求结束进程树后等待其退出、以及主进程退出后等待输出读完的上限。</summary>
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(10), DrainWait = TimeSpan.FromSeconds(5);
    private static readonly Regex AnsiEscape = new("\u001b\\[[0-9;?]*[A-Za-z]", RegexOptions.CultureInvariant);
    private static readonly string[] ProviderKeyVariables = ["ANTHROPIC_API_KEY", "OPENAI_API_KEY"];
    private readonly Func<string, string, CancellationToken, Task<bool>> confirm;
    private readonly string[] hiddenVariables;
    private readonly IReadOnlyDictionary<string, string> knownPrograms;
    private readonly TextCodec codec;

    /// <param name="allowlist">白名单规则行，格式见 <see cref="CommandAllowlist"/>；格式错误时抛出 ArgumentException。</param>
    /// <param name="confirm">每条命令执行前调用（标题、详情）；返回 false 时不执行。取消时应抛出 OperationCanceledException。</param>
    /// <param name="hiddenEnvironmentVariables">不传给子进程的环境变量名（如配置中的密钥变量）；ANTHROPIC_API_KEY 与 OPENAI_API_KEY 总是移除。</param>
    /// <param name="knownPrograms">PATH 之外的程序位置（程序名 → 完整路径，如 VS 自带的 git）；优先于 PATH。</param>
    /// <param name="codec">输出解码；为 null 时按 UTF-8 解码。</param>
    public CommandRunner(IEnumerable<string>? allowlist, Func<string, string, CancellationToken, Task<bool>> confirm,
        IEnumerable<string>? hiddenEnvironmentVariables = null, IReadOnlyDictionary<string, string>? knownPrograms = null, TextCodec? codec = null)
    {
        Rules = CommandAllowlist.Parse(allowlist);
        this.confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
        hiddenVariables = ProviderKeyVariables.Concat(hiddenEnvironmentVariables ?? []).Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        this.knownPrograms = knownPrograms ?? new Dictionary<string, string>();
        this.codec = codec ?? new TextCodec(null);
    }

    public IReadOnlyList<CommandRule> Rules { get; }

    /// <summary>校验白名单与参数、定位程序、请求用户批准，然后执行并等待结束或超时。</summary>
    /// <param name="title">审批面板标题前缀，如“执行命令”。</param>
    public async Task<CommandOutcome> RunAsync(string runId, CommandRequest request, string workspaceRoot, string title, CancellationToken cancellationToken)
    {
        var program = request.Program.Trim();
        var arguments = request.Arguments;
        if (!CommandAllowlist.IsValidProgram(program))
            throw new WorkspaceException("command must be a program name without a path, such as dotnet or git. Only .exe programs can run.");
        if (arguments.Count > MaxArguments) throw new WorkspaceException($"Use at most {MaxArguments} arguments.");
        foreach (var argument in arguments)
            if (argument.Length > MaxArgumentChars || argument.IndexOfAny(['\0', '\r', '\n']) >= 0)
                throw new WorkspaceException($"Each argument must be a single line of at most {MaxArgumentChars} characters.");
        if (request.TimeoutSeconds is < 1 or > MaxTimeoutSeconds) throw new WorkspaceException($"timeout_seconds must be between 1 and {MaxTimeoutSeconds}.");
        if (!CommandAllowlist.Allows(Rules, program, arguments))
            throw new WorkspaceException(Rules.Count == 0
                ? "The user's command allowlist is empty, so no commands can run. Tell the user what you would run and why."
                : $"This command is not on the user's allowlist ({string.Join(", ", Rules)}). The first argument must match the allowed subcommand exactly. " +
                  "Do not try to work around the allowlist; tell the user what you would run so they can add it in Settings.");
        var path = Resolve(CommandAllowlist.NormalizeProgram(program), workspaceRoot)
            ?? throw new WorkspaceException($"Program '{program}' was not found on PATH. Tell the user it is not installed or not on PATH.");
        var argumentLine = string.Join(" ", arguments.Select(Quote));
        if (argumentLine.Length > MaxCommandLineChars) throw new WorkspaceException($"The command line must be at most {MaxCommandLineChars} characters.");
        var display = CommandAllowlist.NormalizeProgram(program) + (argumentLine.Length > 0 ? " " + argumentLine : "");
        var detail = $"命令：{display}\n程序：{path}\n目录：{request.DisplayDirectory}\n超时：{request.TimeoutSeconds} 秒\n\n" +
            "命令以你的权限运行，不经过 shell。它可能执行项目代码（生成事件、测试代码）或仓库配置的程序（git 钩子、过滤器），也可能修改文件；" +
            "这些修改不在备份与撤销范围内。超时或停止时结束整个进程树。确认命令与目录无误后再批准。";
        var subcommand = arguments.Count > 0 ? arguments[0] : "";
        var approved = await confirm(title + "：" + (display.Length > 80 ? display.Substring(0, 80) + "…" : display), detail, cancellationToken).ConfigureAwait(false);
        RunLog.Info(runId, approved ? "command_approved" : "command_rejected", ("program", path), ("subcommand", subcommand), ("args", arguments.Count),
            ("cwd", request.DisplayDirectory));
        if (!approved) throw new WorkspaceException("The user rejected this command. Do not request it again in this run; ask the user what they want instead.");
        cancellationToken.ThrowIfCancellationRequested();
        return await ExecuteAsync(runId, path, argumentLine, display, request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandOutcome> ExecuteAsync(string runId, string path, string argumentLine, string display, CommandRequest request, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(path, argumentLine)
        {
            WorkingDirectory = request.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var name in hiddenVariables) start.EnvironmentVariables.Remove(name);
        start.EnvironmentVariables["DOTNET_NOLOGO"] = "1";
        // 不留常驻 MSBuild 节点：否则它们继承输出管道并在命令结束后继续占用文件。
        start.EnvironmentVariables["MSBUILDDISABLENODEREUSE"] = "1";
        start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        start.EnvironmentVariables["GIT_PAGER"] = "";

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult(true);
        var watch = Stopwatch.StartNew();
        try
        {
            if (!process.Start()) throw new WorkspaceException($"Could not start {display}.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            RunLog.Warn(runId, "command_start_failed", ("program", path), ("error", ex.NativeErrorCode));
            throw new WorkspaceException($"Could not start {display}: {ex.Message}");
        }
        using var tree = ProcessTree.Attach(process, runId);
        try { process.StandardInput.Close(); }
        catch (IOException) { }
        var output = new OutputCapture(process.StandardOutput.BaseStream);
        var error = new OutputCapture(process.StandardError.BaseStream);
        RunLog.Info(runId, "command_start", ("program", path), ("pid", process.Id), ("timeout", request.TimeoutSeconds));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var finished = await WaitAsync(exited.Task, linked.Token).ConfigureAwait(false);
        var stillRunning = false;
        if (!finished)
        {
            // 区分“请求结束”与“已经结束”：结束进程树后等待退出通知，超时则如实报告仍在运行。
            tree.Kill();
            stillRunning = !await WaitAsync(exited.Task, StopWait).ConfigureAwait(false);
        }
        // 主进程已退出但子进程仍持有输出管道时，结束整个进程树，再给输出读取一次机会。
        var drained = Task.WhenAll(output.Completion, error.Completion);
        if (!await WaitAsync(drained, DrainWait).ConfigureAwait(false))
        {
            tree.Kill();
            await WaitAsync(drained, DrainWait).ConfigureAwait(false);
        }
        watch.Stop();

        int? exitCode = finished && !stillRunning ? process.ExitCode : null;
        if (stillRunning)
            RunLog.Write("ERROR", runId, "command_not_stopped", [("program", path), ("pid", process.Id)]);
        if (cancellationToken.IsCancellationRequested)
        {
            RunLog.Warn(runId, "command_canceled", ("program", path), ("ms", watch.ElapsedMilliseconds), ("stillRunning", stillRunning));
            throw new OperationCanceledException(cancellationToken);
        }
        var timedOut = !finished;
        RunLog.Write(exitCode == 0 ? "INFO" : "WARN", runId, "command_end",
            [("program", path), ("exitCode", exitCode), ("timedOut", timedOut), ("stillRunning", stillRunning), ("ms", watch.ElapsedMilliseconds)]);
        var (stdout, stdoutTruncated) = output.Text(codec);
        var (stderr, stderrTruncated) = error.Text(codec);
        return new(display, exitCode, timedOut, stillRunning, watch.ElapsedMilliseconds, stdout, stdoutTruncated, stderr, stderrTruncated);
    }

    /// <summary>等待任务完成；取消或到达时限时返回 false，不抛出异常。</summary>
    private static async Task<bool> WaitAsync(Task task, CancellationToken token)
    {
        if (task.IsCompleted) return true;
        var cancel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (token.Register(() => cancel.TrySetResult(false)))
            return await Task.WhenAny(task, cancel.Task).ConfigureAwait(false) == task;
    }

    private static async Task<bool> WaitAsync(Task task, TimeSpan limit)
    {
        using var timer = new CancellationTokenSource(limit);
        return await WaitAsync(task, timer.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 程序位置：先查 <see cref="knownPrograms"/>，再按 PATH 顺序查找（Windows 上只找 .exe）。忽略相对 PATH 项；位于工作区内的程序不执行，
    /// 以免仓库自带的同名程序被当作系统工具运行。找不到时返回 null。
    /// </summary>
    private string? Resolve(string name, string workspaceRoot)
    {
        var fileName = Compat.IsWindows ? name + ".exe" : name;
        var candidates = new List<string>();
        foreach (var known in knownPrograms)
            if (string.Equals(CommandAllowlist.NormalizeProgram(known.Key), name, StringComparison.OrdinalIgnoreCase)) candidates.Add(known.Value);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var trimmed = directory.Trim().Trim('"');
            if (trimmed.Length == 0 || !Compat.IsPathFullyQualified(trimmed)) continue;
            try { candidates.Add(Path.Combine(trimmed, fileName)); }
            catch (ArgumentException) { }
        }
        var root = Compat.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot)) + Path.DirectorySeparatorChar;
        var comparison = Compat.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var candidate in candidates)
        {
            try
            {
                var full = Path.GetFullPath(candidate);
                if (!File.Exists(full) || full.StartsWith(root, comparison)) continue;
                if (Compat.IsWindows && !full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                return full;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>按 Windows 命令行解析规则（CommandLineToArgvW）引用一个参数，使目标程序收到原样的参数。</summary>
    internal static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0) return argument;
        var builder = new StringBuilder("\"");
        for (var i = 0; i < argument.Length; i++)
        {
            var backslashes = 0;
            while (i < argument.Length && argument[i] == '\\') { backslashes++; i++; }
            if (i == argument.Length) { builder.Append('\\', backslashes * 2); break; }
            if (argument[i] == '"') builder.Append('\\', backslashes * 2 + 1).Append('"');
            else builder.Append('\\', backslashes).Append(argument[i]);
        }
        return builder.Append('"').ToString();
    }

    /// <summary>保留输出的开头与结尾（按字节），中间丢弃；读取失败视为输出结束。</summary>
    private sealed class OutputCapture
    {
        private readonly MemoryStream head = new();
        private readonly byte[] tail = new byte[TailBytes];
        private long tailWritten;
        private readonly object gate = new();

        public OutputCapture(Stream stream) => Completion = Task.Run(() => PumpAsync(stream));

        public Task Completion { get; }

        private async Task PumpAsync(Stream stream)
        {
            var buffer = new byte[8192];
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    lock (gate) Append(buffer, read);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        }

        private void Append(byte[] buffer, int count)
        {
            var offset = 0;
            if (head.Length < HeadBytes)
            {
                var take = (int)Math.Min(count, HeadBytes - head.Length);
                head.Write(buffer, 0, take);
                offset = take;
            }
            for (var i = offset; i < count; i++) tail[tailWritten++ % TailBytes] = buffer[i];
        }

        /// <summary>解码为文本（换行统一为 \n，去掉 ANSI 颜色序列）；被截断时在开头与结尾之间插入说明，两端都在整行处截断。</summary>
        public (string Text, bool Truncated) Text(TextCodec codec)
        {
            byte[] headBytes, tailBytes;
            bool truncated;
            lock (gate)
            {
                headBytes = head.ToArray();
                truncated = tailWritten > TailBytes;
                var length = (int)Math.Min(tailWritten, TailBytes);
                tailBytes = new byte[length];
                var start = truncated ? tailWritten % TailBytes : 0;
                for (var i = 0; i < length; i++) tailBytes[i] = tail[(start + i) % TailBytes];
            }
            if (!truncated) return (Decode(codec, headBytes.Concat(tailBytes).ToArray()), false);
            var headEnd = Array.LastIndexOf(headBytes, (byte)'\n') + 1;
            var tailStart = Array.IndexOf(tailBytes, (byte)'\n') + 1;
            return (Decode(codec, headBytes.Take(headEnd).ToArray()) + "\n[... output truncated ...]\n" + Decode(codec, tailBytes.Skip(tailStart).ToArray()), true);
        }

        private static string Decode(TextCodec codec, byte[] bytes)
        {
            string text;
            try { text = codec.Decode(bytes).Text; }
            catch (WorkspaceException) { text = new UTF8Encoding(false, false).GetString(bytes).Replace("\0", ""); }
            return AnsiEscape.Replace(text.Replace("\r\n", "\n"), "");
        }
    }
}

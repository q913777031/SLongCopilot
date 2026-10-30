using System.Text;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class CommandRunnerTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "SLCopilot-command-tests-" + Guid.NewGuid().ToString("N"));
    private static bool Windows => Path.DirectorySeparatorChar == '\\';

    public CommandRunnerTests() => Directory.CreateDirectory(temp);

    private static Task<bool> Approve(string title, string detail, CancellationToken token) => Task.FromResult(true);

    private Task<CommandOutcome> Run(CommandRunner runner, string program, string[] args, int timeout = 60, CancellationToken token = default)
        => runner.RunAsync("test", new(program, args, temp, ".", timeout), temp, "执行命令", token);

    [Fact]
    public void DefaultsParseAndRulesMatchProgramAndExactSubcommand()
    {
        var rules = CommandAllowlist.Parse(CommandAllowlist.Defaults);
        Assert.Equal(CommandAllowlist.Defaults.Length, rules.Count);
        Assert.True(CommandAllowlist.Allows(rules, "dotnet", ["test", "--filter", "X"]));
        Assert.True(CommandAllowlist.Allows(rules, "DOTNET.exe", ["build"]));
        Assert.False(CommandAllowlist.Allows(rules, "dotnet", ["run"]));
        Assert.False(CommandAllowlist.Allows(rules, "dotnet", []));
        Assert.False(CommandAllowlist.Allows(rules, "dotnet", ["Test"]));
        // 全局选项放在子命令前（如 git -c ...）不匹配“git status”。
        Assert.False(CommandAllowlist.Allows(rules, "git", ["-c", "core.pager=x", "status"]));
        Assert.True(CommandAllowlist.Allows(CommandAllowlist.Parse(["# comment", "", "  msbuild  "]), "msbuild", ["/t:Rebuild"]));
        Assert.Single(CommandAllowlist.Parse(["dotnet test", "dotnet.exe test"]));
    }

    [Theory]
    [InlineData("C:\\tools\\dotnet")]
    [InlineData("./dotnet")]
    [InlineData("build.cmd")]
    [InlineData("run.bat test")]
    [InlineData("dotnet test extra")]
    [InlineData("dotnet $(x)")]
    public void InvalidRulesAreRejected(string rule) => Assert.Throws<ArgumentException>(() => CommandAllowlist.Parse([rule]));

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("C:\\dir with space\\", "\"C:\\dir with space\\\\\"")]
    [InlineData("a\\\\b", "a\\\\b")]
    public void QuoteFollowsWindowsCommandLineRules(string argument, string expected) => Assert.Equal(expected, CommandRunner.Quote(argument));

    [Fact]
    public async Task CommandsOutsideTheAllowlistAreRejectedBeforeApproval()
    {
        var asked = 0;
        var runner = new CommandRunner(["dotnet test"], (_, _, _) => { asked++; return Task.FromResult(true); });
        var error = await Assert.ThrowsAsync<WorkspaceException>(() => Run(runner, "dotnet", ["run"]));
        Assert.Contains("allowlist", error.Message);
        await Assert.ThrowsAsync<WorkspaceException>(() => Run(runner, "/usr/bin/dotnet", ["test"]));
        await Assert.ThrowsAsync<WorkspaceException>(() => Run(runner, "dotnet", ["test", "a\nb"]));
        await Assert.ThrowsAsync<WorkspaceException>(() => Run(runner, "dotnet", ["test"], timeout: CommandRunner.MaxTimeoutSeconds + 1));
        var empty = new CommandRunner([], (_, _, _) => { asked++; return Task.FromResult(true); });
        Assert.Contains("empty", (await Assert.ThrowsAsync<WorkspaceException>(() => Run(empty, "dotnet", ["test"]))).Message);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task RejectedCommandDoesNotRun()
    {
        var marker = Path.Combine(temp, "marker.txt");
        var (program, args) = Windows ? ("cmd", new[] { "/c", "echo x>" + marker }) : ("touch", new[] { marker });
        string? detail = null;
        var runner = new CommandRunner([program], (_, d, _) => { detail = d; return Task.FromResult(false); });
        var error = await Assert.ThrowsAsync<WorkspaceException>(() => Run(runner, program, args));
        Assert.Contains("rejected", error.Message);
        Assert.Contains("不经过 shell", detail);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task RunsAllowedProgramAndReturnsExitCodeAndOutput()
    {
        string? title = null;
        var runner = new CommandRunner(["dotnet --version"], (t, _, _) => { title = t; return Task.FromResult(true); });
        var outcome = await Run(runner, "dotnet", ["--version"]);
        Assert.True(outcome.Succeeded, outcome.StandardError);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Matches("^\\d+\\.\\d+", outcome.StandardOutput.Trim());
        Assert.Equal("执行命令：dotnet --version", title);
        Assert.Equal("dotnet --version", outcome.CommandLine);
    }

    [Fact]
    public async Task NonZeroExitIsReportedAsFailure()
    {
        var runner = new CommandRunner(["dotnet"], Approve);
        var outcome = await Run(runner, "dotnet", ["no-such-command-xyz"]);
        Assert.False(outcome.Succeeded);
        Assert.NotEqual(0, outcome.ExitCode);
        Assert.False(outcome.TimedOut);
    }

    [Fact]
    public async Task ProviderKeysAreNotInheritedByCommands()
    {
        Environment.SetEnvironmentVariable("SLCOPILOT_TEST_SECRET", "s3cr3t-value");
        try
        {
            var (program, args) = Windows ? ("cmd", new[] { "/c", "set" }) : ("env", Array.Empty<string>());
            var runner = new CommandRunner([program], Approve, ["SLCOPILOT_TEST_SECRET"]);
            var outcome = await Run(runner, program, args);
            Assert.True(outcome.Succeeded);
            Assert.DoesNotContain("s3cr3t-value", outcome.StandardOutput);
            Assert.Contains("MSBUILDDISABLENODEREUSE=1", outcome.StandardOutput);
        }
        finally { Environment.SetEnvironmentVariable("SLCOPILOT_TEST_SECRET", null); }
    }

    [Fact]
    public async Task TimeoutTerminatesTheProcessAndReportsNoExitCode()
    {
        var (program, args) = Windows ? ("ping", new[] { "-n", "60", "127.0.0.1" }) : ("sleep", new[] { "60" });
        var runner = new CommandRunner([program], Approve);
        var started = DateTime.UtcNow;
        var outcome = await Run(runner, program, args, timeout: 1);
        Assert.True(outcome.TimedOut);
        Assert.False(outcome.StillRunning);
        Assert.Null(outcome.ExitCode);
        Assert.False(outcome.Succeeded);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task CancellationStopsTheProcess()
    {
        var (program, args) = Windows ? ("ping", new[] { "-n", "60", "127.0.0.1" }) : ("sleep", new[] { "60" });
        var runner = new CommandRunner([program], Approve);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(runner, program, args, token: cts.Token));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task LongOutputKeepsBeginningAndEnd()
    {
        var file = Path.Combine(temp, "big.txt");
        var text = new StringBuilder();
        for (var i = 1; i <= 20000; i++) text.Append("line ").Append(i).Append('\n');
        await File.WriteAllTextAsync(file, text.ToString());
        var (program, args) = Windows ? ("cmd", new[] { "/c", "type", file }) : ("cat", new[] { file });
        var outcome = await Run(new CommandRunner([program], Approve), program, args);
        Assert.True(outcome.OutputTruncated);
        Assert.StartsWith("line 1\n", outcome.StandardOutput);
        Assert.EndsWith("line 20000\n", outcome.StandardOutput);
        Assert.Contains("[... output truncated ...]", outcome.StandardOutput);
        Assert.True(outcome.StandardOutput.Length < 40000);
    }

    [Fact]
    public async Task MissingProgramsAndProgramsInsideTheWorkspaceAreNotRun()
    {
        var runner = new CommandRunner(["no-such-program-xyz"], Approve);
        Assert.Contains("not found", (await Assert.ThrowsAsync<WorkspaceException>(() => Run(runner, "no-such-program-xyz", []))).Message);
        var local = Path.Combine(temp, Windows ? "tool.exe" : "tool");
        await File.WriteAllTextAsync(local, "");
        var inside = new CommandRunner(["tool"], Approve, knownPrograms: new Dictionary<string, string> { ["tool"] = local });
        await Assert.ThrowsAsync<WorkspaceException>(() => Run(inside, "tool", []));
    }

    public void Dispose()
    {
        try { Directory.Delete(temp, true); }
        catch (IOException) { }
    }
}

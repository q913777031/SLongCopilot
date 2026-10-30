using System.Diagnostics;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class GitChangesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SLCopilot-git-" + Guid.NewGuid().ToString("N"));
    private readonly string? git = GitChangeReader.Find([]);

    public GitChangesTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private void Git(string arguments)
    {
        var start = new ProcessStartInfo(git!, "-c user.name=t -c user.email=t@example.com " + arguments)
        { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    [Fact]
    public async Task OnlyStagedChangesAreReadAndUnstagedOnesAreIgnored()
    {
        if (git is null) return; // 没有安装 git 的环境跳过。
        Git("init -q");
        System.IO.File.WriteAllText(Path.Combine(root, "a.txt"), "one\n");
        Git("add a.txt");
        Git("commit -q -m \"feat: add a\"");

        Assert.Null(await GitChangeReader.ReadAsync(git, root, CancellationToken.None));

        // 未暂存的改动不读取（比较工作区文件会执行仓库配置的过滤器程序）。
        System.IO.File.WriteAllText(Path.Combine(root, "a.txt"), "two\n");
        Assert.Null(await GitChangeReader.ReadAsync(git, root, CancellationToken.None));

        Git("add a.txt");
        System.IO.File.WriteAllText(Path.Combine(root, "b.txt"), "untracked\n");
        var staged = (await GitChangeReader.ReadAsync(git, root, CancellationToken.None))!;
        Assert.True(staged.Staged);
        Assert.False(staged.Truncated);
        Assert.Contains("+two", staged.Diff);
        Assert.Equal(["feat: add a"], staged.RecentSubjects);
        Assert.Contains("a.txt", staged.Stat);
        Assert.DoesNotContain("untracked", staged.Diff);
        Assert.Contains("feat: add a", CommitMessagePrompt.Build(staged));
    }

    [Fact]
    public async Task SummaryNamesBranchStagedFilesAndRecentCommits()
    {
        if (git is null) return;
        Assert.Null(await GitChangeReader.SummaryAsync(git, root, CancellationToken.None));
        Git("init -q -b main");
        System.IO.File.WriteAllText(Path.Combine(root, "a.txt"), "one\n");
        Git("add a.txt");
        Git("commit -q -m \"feat: add a\"");
        System.IO.File.WriteAllText(Path.Combine(root, "b.txt"), "two\n");
        Git("add b.txt");
        var summary = await GitChangeReader.SummaryAsync(git, root, CancellationToken.None);
        Assert.StartsWith("Git branch: main; staged: A b.txt; recent commits: ", summary);
        Assert.EndsWith(" feat: add a", summary);
    }

    [Fact]
    public async Task HugeStagedDiffsAreTruncatedWithANote()
    {
        if (git is null) return;
        Git("init -q");
        System.IO.File.WriteAllText(Path.Combine(root, "big.txt"), string.Concat(Enumerable.Range(0, 30_000).Select(i => $"line {i:D6} ..........\n")));
        Git("add big.txt");
        var changes = (await GitChangeReader.ReadAsync(git, root, CancellationToken.None))!;
        Assert.True(changes.Truncated);
        Assert.Equal(GitChangeReader.MaxDiffChars, changes.Diff.Length);
        Assert.Contains("truncated", CommitMessagePrompt.Build(changes));
    }

    [Fact]
    public async Task NonRepositoriesFailWithGitError()
    {
        if (git is null) return;
        var error = await Assert.ThrowsAsync<IOException>(() => GitChangeReader.ReadAsync(git, root, CancellationToken.None));
        Assert.StartsWith("git failed", error.Message);
    }

    [Fact]
    public void FencesAreRemovedFromGeneratedMessages()
    {
        Assert.Equal("fix: x\n\nbody", CommitMessagePrompt.Clean("```text\nfix: x\n\nbody\n```"));
        Assert.Equal("fix: y", CommitMessagePrompt.Clean("  fix: y  "));
    }
}

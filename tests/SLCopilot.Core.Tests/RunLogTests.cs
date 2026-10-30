using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class RunLogTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SLCopilot-log-" + Guid.NewGuid().ToString("N"));

    public RunLogTests() => RunLog.Directory = directory;

    public void Dispose()
    {
        RunLog.Directory = null;
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private string[] Lines(string run) => Directory.GetFiles(directory, "slcopilot-*.log")
        .SelectMany(f => System.IO.File.ReadAllLines(f)).Where(l => l.Contains(" run=" + run + " ")).ToArray();

    [Fact]
    public void EventsAreSingleLineKeyValuePairsWithTruncatedValues()
    {
        var run = RunLog.NewRunId();
        RunLog.Info(run, "tool", ("name", "read_file"), ("path", "src/My File.cs"), ("ok", true), ("ms", 12), ("note", "a\nb=c \"q\""), ("long", new string('x', 500)), ("none", null));
        var line = Assert.Single(Lines(run));
        Assert.Contains(" INFO run=" + run + " event=tool name=read_file path=\"src/My File.cs\" ok=true ms=12 note=\"a b=c 'q'\"", line);
        Assert.Contains("x…", line);
        Assert.Contains("none=-", line);
        Assert.DoesNotContain(new string('x', 301), line);
    }

    [Fact]
    public void ErrorsIncludeTypeMessageAndStackOnOneLine()
    {
        var run = RunLog.NewRunId();
        try { throw new InvalidOperationException("boom"); }
        catch (InvalidOperationException ex) { RunLog.Error(run, "agent_failed", ex, ("status", 429)); }
        var line = Assert.Single(Lines(run));
        Assert.Contains(" ERROR ", line);
        Assert.Contains("status=429 error=InvalidOperationException message=boom stack=", line);
    }

    [Fact]
    public void NothingIsWrittenWithoutADirectory()
    {
        RunLog.Directory = null;
        var run = RunLog.NewRunId();
        RunLog.Info(run, "x");
        // 其他测试类可能并行写日志，只检查本条事件不存在。
        Assert.True(!Directory.Exists(directory) || Lines(run).Length == 0);
    }
}

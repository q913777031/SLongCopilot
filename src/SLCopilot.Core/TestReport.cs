using System.Xml;
using System.Xml.Linq;

namespace SLCopilot.Core;

/// <summary>一个未通过的测试。<see cref="Outcome"/> 为 TRX 中的结果（Failed、Error、Timeout、Aborted）。</summary>
public sealed record TestFailure(string Name, string Outcome, string Message, string StackTrace);

/// <summary>
/// 一次测试运行的汇总。<see cref="Failed"/> 含失败、错误、超时与中止；<see cref="Skipped"/> 为未执行的测试。
/// <see cref="Files"/> 为读到的 TRX 文件数；为 0 表示没有产生结果（通常是编译失败或没有找到测试项目）。
/// </summary>
public sealed record TestReport(int Total, int Passed, int Failed, int Skipped, IReadOnlyList<TestFailure> Failures, int Files);

/// <summary>读取 dotnet test 生成的 TRX（Visual Studio 测试结果）文件。禁用 DTD 与外部实体；单个文件超过 64 MiB 时跳过。</summary>
public static class TestReportReader
{
    private const long MaxFileBytes = 64L * 1024 * 1024;
    private const int MaxMessageChars = 2000, MaxStackChars = 3000;
    private static readonly HashSet<string> FailedOutcomes = new(StringComparer.OrdinalIgnoreCase) { "Failed", "Error", "Timeout", "Aborted" };

    /// <summary>汇总目录（含子目录）中的全部 TRX 文件，最多返回 <paramref name="maxFailures"/> 个失败的详细信息。</summary>
    public static TestReport Read(string directory, int maxFailures)
    {
        var reports = new List<TestReport>();
        if (Directory.Exists(directory))
            foreach (var file in Directory.EnumerateFiles(directory, "*.trx", SearchOption.AllDirectories))
            {
                if (new FileInfo(file).Length > MaxFileBytes) continue;
                using var stream = File.OpenRead(file);
                reports.Add(Parse(stream, maxFailures));
            }
        return new(reports.Sum(r => r.Total), reports.Sum(r => r.Passed), reports.Sum(r => r.Failed), reports.Sum(r => r.Skipped),
            reports.SelectMany(r => r.Failures).Take(maxFailures).ToList(), reports.Count);
    }

    /// <summary>解析一个 TRX 文件。优先使用 ResultSummary/Counters 的计数；没有时按 UnitTestResult 统计。XML 无效时抛出 XmlException。</summary>
    public static TestReport Parse(Stream stream, int maxFailures)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(stream, settings);
        var document = XDocument.Load(reader);
        var ns = document.Root?.Name.Namespace ?? XNamespace.None;
        var results = document.Descendants(ns + "UnitTestResult").ToList();
        var failures = results.Where(r => FailedOutcomes.Contains((string?)r.Attribute("outcome") ?? "")).Take(maxFailures).Select(r =>
        {
            var info = r.Element(ns + "Output")?.Element(ns + "ErrorInfo");
            return new TestFailure((string?)r.Attribute("testName") ?? "", (string?)r.Attribute("outcome") ?? "",
                Truncate(info?.Element(ns + "Message")?.Value, MaxMessageChars), Truncate(info?.Element(ns + "StackTrace")?.Value, MaxStackChars));
        }).ToList();
        if (document.Descendants(ns + "Counters").FirstOrDefault() is { } counters)
        {
            int Count(string name) => int.TryParse((string?)counters.Attribute(name), out var n) ? n : 0;
            return new(Count("total"), Count("passed"), Count("failed") + Count("error") + Count("timeout") + Count("aborted"),
                Count("notExecuted") + Count("notRunnable"), failures, 1);
        }
        var outcomes = results.Select(r => (string?)r.Attribute("outcome") ?? "").ToList();
        return new(outcomes.Count, outcomes.Count(o => o == "Passed"), outcomes.Count(FailedOutcomes.Contains),
            outcomes.Count(o => o is "NotExecuted" or "Inconclusive"), failures, 1);
    }

    private static string Truncate(string? text, int max)
    {
        var value = (text ?? "").Replace("\r\n", "\n").Trim();
        return value.Length > max ? value.Substring(0, max) + "…" : value;
    }
}

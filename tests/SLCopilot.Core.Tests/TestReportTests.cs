using System.Text;
using System.Xml;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class TestReportTests
{
    private const string Trx = """
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun id="1" name="run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testName="Orders.SaveRejectsNull" outcome="Passed" />
            <UnitTestResult testName="Orders.TotalIsRounded" outcome="Failed">
              <Output>
                <ErrorInfo>
                  <Message>Assert.Equal() Failure
        Expected: 10.5
        Actual:   10.49</Message>
                  <StackTrace>   at Orders.TotalIsRounded() in C:\src\OrdersTests.cs:line 42</StackTrace>
                </ErrorInfo>
              </Output>
            </UnitTestResult>
            <UnitTestResult testName="Orders.Slow" outcome="NotExecuted" />
          </Results>
          <ResultSummary outcome="Failed">
            <Counters total="3" executed="2" passed="1" failed="1" error="0" timeout="0" aborted="0" notExecuted="1" />
          </ResultSummary>
        </TestRun>
        """;

    private static TestReport Parse(string xml) => TestReportReader.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml.Trim())), 30);

    [Fact]
    public void CountersAndFailureDetailsAreRead()
    {
        var report = Parse(Trx);
        Assert.Equal((3, 1, 1, 1), (report.Total, report.Passed, report.Failed, report.Skipped));
        var failure = Assert.Single(report.Failures);
        Assert.Equal("Orders.TotalIsRounded", failure.Name);
        Assert.Contains("Expected: 10.5", failure.Message);
        Assert.Contains("OrdersTests.cs:line 42", failure.StackTrace);
    }

    [Fact]
    public void ResultsWithoutCountersAreCounted()
    {
        var report = Parse(Trx.Replace("<Counters total=\"3\" executed=\"2\" passed=\"1\" failed=\"1\" error=\"0\" timeout=\"0\" aborted=\"0\" notExecuted=\"1\" />", ""));
        Assert.Equal((3, 1, 1, 1), (report.Total, report.Passed, report.Failed, report.Skipped));
    }

    [Fact]
    public void DtdIsRejected()
        => Assert.Throws<XmlException>(() => Parse("<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e \"y\">]><TestRun>&e;</TestRun>"));

    [Fact]
    public void MissingDirectoryMeansNoResults()
    {
        var report = TestReportReader.Read(Path.Combine(Path.GetTempPath(), "SLCopilot-no-such-" + Guid.NewGuid().ToString("N")), 30);
        Assert.Equal(0, report.Files);
        Assert.Equal(0, report.Total);
    }
}

using System.Text.Json.Nodes;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class AgentPlanTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SLCopilot-plan-" + Guid.NewGuid().ToString("N"));
    public AgentPlanTests() => Directory.CreateDirectory(Path.Combine(root, "project"));
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    [Fact]
    public void PlansParseStatusesAndRenderAChecklist()
    {
        var steps = AgentPlan.Parse("done: 读取 OrderService.cs\n- doing：增加参数校验\n\nTODO: Build and fix errors\n");
        Assert.Equal([new PlanStep("读取 OrderService.cs", "done"), new PlanStep("增加参数校验", "doing"), new PlanStep("Build and fix errors", "todo")], steps);
        var markdown = AgentPlan.ToMarkdown(steps);
        Assert.Contains("已完成 1/3", markdown);
        Assert.Contains("- ▶ 增加参数校验", markdown);
        Assert.Equal(steps, AgentPlan.Deserialize(AgentPlan.Serialize(steps)));
    }

    [Fact]
    public void InvalidPlansAreRejectedWithFormatHelp()
    {
        Assert.Contains("done:", Assert.Throws<WorkspaceException>(() => AgentPlan.Parse("step one")).Message);
        Assert.Throws<WorkspaceException>(() => AgentPlan.Parse("  "));
        Assert.Throws<WorkspaceException>(() => AgentPlan.Parse("doing: a\ndoing: b"));
        Assert.Throws<WorkspaceException>(() => AgentPlan.Parse(string.Join("\n", Enumerable.Range(0, AgentPlan.MaxSteps + 1).Select(i => "todo: s" + i))));
    }

    [Fact]
    public async Task UpdatePlanToolReportsThePlanWithoutTouchingTheWorkspace()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(() => ++calls == 1
            ? "{\"content\":[{\"type\":\"tool_use\",\"id\":\"p1\",\"name\":\"update_plan\",\"input\":{\"plan\":\"doing: inspect\\ntodo: change\"}},{\"type\":\"tool_use\",\"id\":\"p2\",\"name\":\"update_plan\",\"input\":{\"plan\":\"oops\"}}],\"stop_reason\":\"tool_use\"}"
            : "{\"content\":[{\"type\":\"text\",\"text\":\"done\"}],\"stop_reason\":\"end_turn\"}"));
        var events = new List<AgentEvent>();
        var settings = new ProviderSettings { Provider = ProviderKind.Claude, Endpoint = "https://gateway.example.com/v1", Model = "claude-opus-5-5" };
        var workspace = new WorkspaceTools(Path.Combine(root, "project"), false, Path.Combine(root, "backups"));
        await new AgentRunner(new AgentProtocol(http)).RunAsync(settings, "key", "task", workspace, events.Add, CancellationToken.None);
        var plan = Assert.Single(events, e => e.Kind == "plan");
        Assert.Equal(2, AgentPlan.Deserialize(plan.Message).Count);
        Assert.DoesNotContain(events, e => e.Kind == "tool" && e.Message.StartsWith("update_plan", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == "error" && e.Message.Contains("done:"));
        Assert.Equal(0, workspace.ChangeCount);
    }

    private sealed class Handler(Func<string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent(Sse.Convert(respond()), System.Text.Encoding.UTF8, "text/event-stream") });
    }
}

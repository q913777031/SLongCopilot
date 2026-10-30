using System.Text.Json;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

/// <summary>多次运行检查点依赖的行为：按逆序撤销可回到任意运行之前；顺序错误时拒绝且不改文件。实际读写文件，需在 Windows 上运行。</summary>
public sealed class CheckpointTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "SLCopilot-checkpoint-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(temp, "project");
    private string FilePath => Path.Combine(Root, "A.cs");

    public CheckpointTests()
    {
        Directory.CreateDirectory(Root);
        System.IO.File.WriteAllText(FilePath, "v1\n");
    }

    public void Dispose() { try { Directory.Delete(temp, true); } catch (IOException) { } }

    private async Task<WorkspaceTools> RunAsync(string content)
    {
        var tools = new WorkspaceTools(Root, true, Path.Combine(temp, "backups"));
        var read = await tools.ExecuteAsync("read_file", JsonSerializer.SerializeToElement(new { path = "A.cs" }), CancellationToken.None);
        var hash = JsonDocument.Parse(read.Content).RootElement.GetProperty("sha256").GetString();
        var write = await tools.ExecuteAsync("write_file", JsonSerializer.SerializeToElement(new { path = "A.cs", content, expected_sha256 = hash }), CancellationToken.None);
        Assert.False(write.IsError, write.Content);
        return tools;
    }

    [Fact]
    public async Task ReverseUndoRestoresAnEarlierCheckpointAndWrongOrderIsRefused()
    {
        var first = await RunAsync("v2\n");
        var second = await RunAsync("v3\n");
        Assert.NotEqual(first.RunId, second.RunId);

        await Assert.ThrowsAsync<WorkspaceException>(() => first.UndoAsync(CancellationToken.None));
        Assert.Equal("v3\n", System.IO.File.ReadAllText(FilePath));
        Assert.Equal(1, first.ChangeCount);

        await second.UndoAsync(CancellationToken.None);
        Assert.Equal("v2\n", System.IO.File.ReadAllText(FilePath));
        await first.UndoAsync(CancellationToken.None);
        Assert.Equal("v1\n", System.IO.File.ReadAllText(FilePath));
        Assert.Equal(0, first.ChangeCount + second.ChangeCount);
    }
}

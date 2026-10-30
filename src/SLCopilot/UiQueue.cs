using System.Collections.Concurrent;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace SLCopilot;

/// <summary>
/// 把后台线程产生的界面更新按提交顺序在 UI 线程执行。任何线程都可调用 <see cref="Post"/>；
/// 单个动作抛出的异常写入 ActivityLog，不影响后续动作。
/// </summary>
public sealed class UiQueue(JoinableTaskFactory joinableTaskFactory)
{
    private readonly ConcurrentQueue<Action> actions = new();
    private int scheduled;

    public void Post(Action action)
    {
        actions.Enqueue(action);
        if (Interlocked.Exchange(ref scheduled, 1) == 0)
            joinableTaskFactory.RunAsync(DrainAsync).FileAndForget("SLCopilot/UiQueue");
    }

    /// <summary>切换到 UI 线程并执行所有已提交的动作。调用方在后台工作结束后使用，保证随后读取的界面状态完整。</summary>
    public async Task FlushAsync()
    {
        await joinableTaskFactory.SwitchToMainThreadAsync();
        RunPending();
    }

    private async Task DrainAsync()
    {
        await joinableTaskFactory.SwitchToMainThreadAsync();
        Volatile.Write(ref scheduled, 0);
        RunPending();
    }

    private void RunPending()
    {
        while (actions.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { ActivityLog.LogError("SLCopilot", $"UI update failed: {ex}"); }
        }
    }
}

using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace SLCopilot;

/// <summary>
/// 进程内扩展入口：注册工具窗口与 Tools 菜单命令，持有唯一的 <see cref="ChatViewModel"/>。
/// 后台加载；命令回调在 UI 线程执行，异常写入 ActivityLog，不向 Visual Studio 抛出。
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuidString)]
[ProvideMenuResource("Menus.ctmenu", 3)]
[ProvideToolWindow(typeof(ChatToolWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids80.SolutionExplorer, Orientation = ToolWindowOrientation.Right)]
public sealed class SLCopilotPackage : AsyncPackage
{
    public const string PackageGuidString = "3f2b8c1a-5d4e-4f6a-9b7c-1e2d3c4b5a60";
    private static readonly Guid CommandSet = new("3f2b8c1a-5d4e-4f6a-9b7c-1e2d3c4b5a61");
    private const int OpenChatId = 0x0100, AttachSelectionId = 0x0101, ExplainSelectionId = 0x0103, FixErrorsId = 0x0104, DiagnosticsId = 0x0105, AnalyzeDebugId = 0x0106;

    private ChatViewModel? viewModel;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var ide = new IdeServices(this);
        viewModel = new ChatViewModel(JoinableTaskFactory, ide) { ShowWindowAsync = ShowChatAsync };
        if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands)
        {
            commands.AddCommand(new MenuCommand((_, _) => Run("OpenChat", ShowChatAsync), new CommandID(CommandSet, OpenChatId)));
            commands.AddCommand(new MenuCommand((_, _) => Run("AttachSelection", AttachSelectionAsync), new CommandID(CommandSet, AttachSelectionId)));
            commands.AddCommand(new MenuCommand((_, _) => Run("ExplainSelection", ExplainSelectionAsync), new CommandID(CommandSet, ExplainSelectionId)));
            commands.AddCommand(new MenuCommand((_, _) => Run("FixErrors", FixErrorsAsync), new CommandID(CommandSet, FixErrorsId)));
            commands.AddCommand(new MenuCommand((_, _) => Run("Diagnostics", DiagnosticsAsync), new CommandID(CommandSet, DiagnosticsId)));
            commands.AddCommand(new MenuCommand((_, _) => Run("AnalyzeDebug", AnalyzeDebugAsync), new CommandID(CommandSet, AnalyzeDebugId)));
        }
    }

    public override IVsAsyncToolWindowFactory GetAsyncToolWindowFactory(Guid toolWindowType)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return toolWindowType == typeof(ChatToolWindow).GUID ? this : base.GetAsyncToolWindowFactory(toolWindowType);
    }

    protected override string GetToolWindowTitle(Type toolWindowType, int id)
        => toolWindowType == typeof(ChatToolWindow) ? "SLCopilot" : base.GetToolWindowTitle(toolWindowType, id);

    protected override Task<object> InitializeToolWindowAsync(Type toolWindowType, int id, CancellationToken cancellationToken)
        => Task.FromResult<object>(viewModel ?? throw new InvalidOperationException("SLCopilot package is not initialized."));

    /// <summary>VS 关闭时停止 Claude CLI 桥接：不再接受连接，进行中的工具调用被取消。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) viewModel?.Bridge.Dispose();
        base.Dispose(disposing);
    }

    private void Run(string name, Func<Task> action)
    {
        JoinableTaskFactory.RunAsync(async () =>
        {
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                ActivityLog.LogError("SLCopilot", $"Command {name} failed: {ex}");
                if (viewModel is not null) viewModel.Status = $"{name} 执行失败：{ex.Message}";
            }
        }).FileAndForget($"SLCopilot/command/{name}");
    }

    private async Task ShowChatAsync()
    {
        var window = await ShowToolWindowAsync(typeof(ChatToolWindow), 0, create: true, DisposalToken);
        if (window?.Frame is null) throw new NotSupportedException("Cannot create the SLCopilot tool window.");
    }

    private async Task AttachSelectionAsync()
    {
        if (viewModel is null) return;
        await viewModel.AttachActiveSelectionAsync();
        await ShowChatAsync();
    }

    private async Task ExplainSelectionAsync()
    {
        if (viewModel is null) return;
        await ShowChatAsync();
        await viewModel.ExplainActiveSelectionAsync();
    }

    private async Task FixErrorsAsync()
    {
        if (viewModel is null) return;
        await ShowChatAsync();
        await viewModel.FixErrorsAtCaretAsync();
    }

    private async Task DiagnosticsAsync()
    {
        if (viewModel is null) return;
        await ShowChatAsync();
        await viewModel.RunDiagnosticsAsync();
    }

    private async Task AnalyzeDebugAsync()
    {
        if (viewModel is null) return;
        await ShowChatAsync();
        await viewModel.AnalyzeDebugBreakAsync();
    }
}

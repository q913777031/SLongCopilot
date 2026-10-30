using System.Text;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using SLCopilot.Core;
using Task = System.Threading.Tasks.Task;

namespace SLCopilot;

/// <summary>附加的选区或插入点。<see cref="Start"/> 为选区在文档中的起始位置。</summary>
public sealed record SelectionTarget(string FilePath, int Start, string Original);

/// <summary>
/// 最近活动编辑器的状态：文件、光标行，以及有选区时的起止行与选中文本（行号从 1 开始）。选区过大时 <see cref="SelectedText"/> 为 null。
/// </summary>
public sealed record EditorContext(string FilePath, int CaretLine, int SelectionStartLine, int SelectionEndLine, string? SelectedText)
{
    public bool HasSelection => SelectionEndLine > 0;
}

/// <summary>一次差异窗口审阅：窗口、临时目录与右侧（可编辑）文件路径。</summary>
public sealed record DiffSession(IVsWindowFrame? Frame, string Directory, string RightPath);

/// <summary>
/// Visual Studio 服务封装。所有方法内部切换到 UI 线程；失败以异常或 null 返回，由调用方转换为界面状态。
/// </summary>
public sealed class IdeServices(AsyncPackage package)
{
    private const int MaxDocumentChars = 2_000_000;

    /// <summary>
    /// 运行结束通知：把摘要写入 VS 状态栏；VS 主窗口不在前台时闪烁任务栏图标，直到用户切回（与 Claude Code 的完成提醒一致）。
    /// 通知失败只记日志，不影响运行结果。
    /// </summary>
    public async Task NotifyFinishedAsync(string text)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            if (await package.GetServiceAsync(typeof(SVsStatusbar)) is IVsStatusbar statusbar)
            {
                await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                statusbar.SetText(text);
            }
            var window = System.Windows.Application.Current?.MainWindow;
            if (window is null) return;
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || NativeMethods.GetForegroundWindow() == handle) return;
            var info = new NativeMethods.FlashInfo
            {
                Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.FlashInfo>(),
                Window = handle,
                Flags = NativeMethods.FlashAll | NativeMethods.FlashUntilForeground,
            };
            NativeMethods.FlashWindowEx(ref info);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            ActivityLog.LogWarning("SLCopilot", $"Finish notification failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 调试器处于中断状态时读取当前异常（$exception）、当前线程调用栈与当前帧局部变量；未在调试或未中断时返回 null。
    /// 单个表达式求值失败只跳过该项。表达式求值超时 1 秒，避免卡住 UI 线程过久。
    /// </summary>
    public async Task<DebugSnapshot?> GetDebugSnapshotAsync(CancellationToken cancellationToken)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (await package.GetServiceAsync(typeof(EnvDTE.DTE)) is not EnvDTE80.DTE2 dte) return null;
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var debugger = dte.Debugger;
        if (debugger is null || debugger.CurrentMode != EnvDTE.dbgDebugMode.dbgBreakMode) return null;

        EnvDTE.Expression? Evaluate(string expression)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return debugger.GetExpression(expression, false, 1000) is { IsValidValue: true } result ? result : null; }
            catch (System.Runtime.InteropServices.COMException) { return null; }
        }

        var exception = Evaluate("$exception");
        string? message = null, trace = null, inner = null;
        if (exception is not null)
        {
            message = Evaluate("$exception.Message")?.Value;
            trace = Evaluate("$exception.StackTrace")?.Value;
            inner = Evaluate("$exception.InnerException")?.Value;
        }
        var frames = new List<DebugFrame>();
        try
        {
            foreach (EnvDTE.StackFrame frame in debugger.CurrentThread.StackFrames)
            {
                if (frames.Count > DebugReport.MaxFrames) break;
                var (file, line) = frame is EnvDTE90a.StackFrame2 located ? (located.FileName, (int)located.LineNumber) : (null, 0);
                frames.Add(new(frame.FunctionName ?? "", string.IsNullOrEmpty(file) ? null : file, line, frame.Module ?? ""));
            }
        }
        catch (System.Runtime.InteropServices.COMException ex) { ActivityLog.LogWarning("SLCopilot", $"Reading the call stack failed: {ex.Message}"); }
        var locals = new List<DebugValue>();
        try
        {
            foreach (EnvDTE.Expression local in debugger.CurrentStackFrame.Locals)
            {
                if (locals.Count > DebugReport.MaxLocals) break;
                locals.Add(new(local.Name ?? "", local.Type ?? "", local.Value ?? ""));
            }
        }
        catch (System.Runtime.InteropServices.COMException ex) { ActivityLog.LogWarning("SLCopilot", $"Reading locals failed: {ex.Message}"); }
        return new(exception?.Type, message, trace, inner, frames, locals);
    }

    private static class NativeMethods
    {
        public const uint FlashAll = 0x3, FlashUntilForeground = 0xC;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct FlashInfo
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool FlashWindowEx(ref FlashInfo info);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
    }

    /// <summary>
    /// 在 VS 差异窗口中打开一次待批准的改动：左侧为修改前（只读临时文件），右侧为模型建议（可编辑临时文件）。
    /// 临时文件位于 %TEMP%\SLCopilot\diff\{id}，按原扩展名命名以保留着色，UTF-8 带 BOM。由 <see cref="CloseDiffAsync"/> 关闭并删除。
    /// </summary>
    public async Task<DiffSession> OpenDiffAsync(string relativePath, string before, string after, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SLCopilot", "diff", Guid.NewGuid().ToString("N"));
        var name = Path.GetFileName(relativePath.Replace('/', Path.DirectorySeparatorChar));
        var left = Path.Combine(directory, "before", name);
        var right = Path.Combine(directory, "after", name);
        Directory.CreateDirectory(Path.GetDirectoryName(left)!);
        Directory.CreateDirectory(Path.GetDirectoryName(right)!);
        var utf8 = new UTF8Encoding(true);
        File.WriteAllText(left, before, utf8);
        File.WriteAllText(right, after, utf8);
        File.SetAttributes(left, File.GetAttributes(left) | FileAttributes.ReadOnly);
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (await package.GetServiceAsync(typeof(SVsDifferenceService)) is not IVsDifferenceService diff)
            throw new InvalidOperationException("Visual Studio 差异服务不可用。");
        var frame = diff.OpenComparisonWindow2(left, right, "SLCopilot：" + name, relativePath,
            "修改前（只读）", "智能体建议（可编辑，批准时写入此内容）", "", "", (uint)__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary);
        return new DiffSession(frame, directory, right);
    }

    /// <summary>
    /// 在差异窗口中查看智能体已完成的改动：左侧为修改前的内容（临时只读文件），右侧为磁盘上的当前文件（可直接编辑）；
    /// <paramref name="currentPath"/> 为 null（文件已删除）时右侧为空的临时文件。窗口关闭时 VS 删除临时文件。
    /// </summary>
    public async Task OpenChangeDiffAsync(string relativePath, string before, string? currentPath, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SLCopilot", "diff", Guid.NewGuid().ToString("N"));
        var name = Path.GetFileName(relativePath.Replace('/', Path.DirectorySeparatorChar));
        var left = Path.Combine(directory, "before", name);
        Directory.CreateDirectory(Path.GetDirectoryName(left)!);
        File.WriteAllText(left, before, new UTF8Encoding(true));
        File.SetAttributes(left, File.GetAttributes(left) | FileAttributes.ReadOnly);
        var options = (uint)__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary;
        var right = currentPath;
        if (right is null)
        {
            right = Path.Combine(directory, "deleted", name);
            Directory.CreateDirectory(Path.GetDirectoryName(right)!);
            File.WriteAllText(right, "", new UTF8Encoding(true));
            File.SetAttributes(right, File.GetAttributes(right) | FileAttributes.ReadOnly);
            options |= (uint)__VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary;
        }
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (await package.GetServiceAsync(typeof(SVsDifferenceService)) is not IVsDifferenceService diff)
            throw new InvalidOperationException("Visual Studio 差异服务不可用。");
        diff.OpenComparisonWindow2(left, right, "SLCopilot：" + name + " 的改动", relativePath,
            "修改前（只读）", currentPath is null ? "已删除" : "当前文件", "", "", options);
    }

    /// <summary>关闭差异窗口（不保存）并删除临时文件；失败只记录日志。</summary>
    public async Task CloseDiffAsync(DiffSession session)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        try { session.Frame?.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            ActivityLog.LogWarning("SLCopilot", $"Closing the diff window failed: {ex.Message}");
        }
        try
        {
            if (Directory.Exists(session.Directory))
            {
                foreach (var file in Directory.GetFiles(session.Directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(session.Directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActivityLog.LogWarning("SLCopilot", $"Deleting diff temp files failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 自检用的环境信息：VS 版本、Roslyn 已加载的 C#/VB 项目数、行内补全组件是否已被编辑器发现。
    /// 单项获取失败时对应值为 null，不抛出。
    /// </summary>
    public async Task<(string? VsVersion, int? RoslynProjects, bool? ProposalSourceRegistered)> GetEnvironmentAsync(CancellationToken cancellationToken)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        string? version = null;
        if (await package.GetServiceAsync(typeof(SVsShell)) is IVsShell shell &&
            ErrorHandler.Succeeded(shell.GetProperty((int)__VSSPROPID5.VSSPROPID_ReleaseVersion, out var value)))
            version = value as string;
        int? projects = null;
        bool? registered = null;
        if (await package.GetServiceAsync(typeof(SComponentModel)) is IComponentModel componentModel)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            try
            {
                projects = componentModel.GetService<Microsoft.VisualStudio.LanguageServices.VisualStudioWorkspace>().CurrentSolution.Projects
                    .Count(p => p.Language is "C#" or "Visual Basic");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Composition.ImportCardinalityMismatchException)
            {
                ActivityLog.LogWarning("SLCopilot", $"Reading the Roslyn workspace failed: {ex.Message}");
            }
            try
            {
                registered = componentModel.GetExtensions<Microsoft.VisualStudio.Language.Proposals.ProposalSourceProviderBase>()
                    .Any(p => p is InlineProposalSourceProvider);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Composition.CompositionException)
            {
                ActivityLog.LogWarning("SLCopilot", $"Reading proposal sources failed: {ex.Message}");
            }
        }
        return (version, projects, registered);
    }

    /// <summary>显示“是 / 否”确认框，默认按钮为“否”。只能在 UI 线程调用。</summary>
    public bool Confirm(string title, string message)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return VsShellUtilities.ShowMessageBox(package, message, title, OLEMSGICON.OLEMSGICON_QUERY,
            OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND) == 6; // IDYES
    }

    /// <summary>创建智能体使用的 IDE 能力（编辑器读写、编译、错误列表）；<paramref name="confirm"/> 用于编译前向用户确认。</summary>
    public Core.IIdeHost CreateAgentHost(Func<string, string, CancellationToken, Task<bool>> confirm) => new AgentIdeHost(package, confirm);

    /// <summary>返回当前解决方案目录；未打开解决方案时返回 null。</summary>
    public async Task<string?> GetSolutionDirectoryAsync(CancellationToken cancellationToken)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (await package.GetServiceAsync(typeof(SVsSolution)) is not IVsSolution solution) return null;
        if (ErrorHandler.Failed(solution.GetSolutionInfo(out var directory, out _, out _))) return null;
        return !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) ? directory : null;
    }

    /// <summary>读取活动编辑器的选区（无选区时为插入点）。没有活动文本编辑器或文档过大时抛出 InvalidOperationException。</summary>
    public async Task<SelectionTarget> GetActiveSelectionAsync(CancellationToken cancellationToken)
    {
        var view = await GetActiveViewAsync(cancellationToken) ?? throw new InvalidOperationException("请先打开一个文本文件。");
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var path = GetFilePath(view) ?? throw new InvalidOperationException("当前编辑器不是磁盘上的文件。");
        var snapshot = view.TextBuffer.CurrentSnapshot;
        if (snapshot.Length > MaxDocumentChars) throw new InvalidOperationException("文件超过 200 万字符。");
        var span = view.Selection.IsEmpty
            ? new SnapshotSpan(view.Caret.Position.BufferPosition, 0)
            : view.Selection.StreamSelectionSpan.SnapshotSpan.TranslateTo(snapshot, SpanTrackingMode.EdgeInclusive);
        return new SelectionTarget(path, span.Start.Position, span.GetText());
    }

    /// <summary>返回最近活动编辑器的文件路径与光标所在行（从 1 开始）；没有可用编辑器时返回 null。</summary>
    public async Task<(string Path, int Line)?> GetCaretLocationAsync(CancellationToken cancellationToken)
    {
        var view = await GetActiveViewAsync(cancellationToken, mustHaveFocus: false);
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (view is null || view.IsClosed || GetFilePath(view) is not { } path) return null;
        return (path, view.Caret.Position.BufferPosition.GetContainingLine().LineNumber + 1);
    }

    /// <summary>
    /// 返回最近活动的文本编辑器的状态（不要求编辑器有焦点：在工具窗口中发送时焦点不在编辑器）。没有可用编辑器或不是磁盘文件时返回 null。
    /// 选区超过 <paramref name="maxSelectionChars"/> 个字符时只给行号。
    /// </summary>
    public async Task<EditorContext?> GetEditorContextAsync(int maxSelectionChars, CancellationToken cancellationToken)
    {
        var view = await GetActiveViewAsync(cancellationToken, mustHaveFocus: false);
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (view is null || view.IsClosed || GetFilePath(view) is not { } path) return null;
        var caret = view.Caret.Position.BufferPosition.GetContainingLine().LineNumber + 1;
        if (view.Selection.IsEmpty) return new EditorContext(path, caret, 0, 0, null);
        var span = view.Selection.StreamSelectionSpan.SnapshotSpan;
        if (span.Length == 0) return new EditorContext(path, caret, 0, 0, null);
        var first = span.Start.GetContainingLine().LineNumber + 1;
        // 选区止于下一行行首时不计入该行。
        var endPoint = span.End.Position > span.Start.Position && span.End.GetContainingLine().Start == span.End ? span.End - 1 : span.End;
        var last = endPoint.GetContainingLine().LineNumber + 1;
        return new EditorContext(path, caret, first, last, span.Length <= maxSelectionChars ? span.GetText() : null);
    }

    /// <summary>当前在 VS 中打开的、位于 <paramref name="root"/> 下的文档（完整路径），活动文档在前；读取失败的文档跳过。</summary>
    public async Task<IReadOnlyList<string>> GetOpenDocumentsAsync(string root, CancellationToken cancellationToken)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (await package.GetServiceAsync(typeof(EnvDTE.DTE)) is not EnvDTE80.DTE2 dte) return [];
        var prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var result = new List<string>();
        try
        {
            if (dte.ActiveDocument?.FullName is { Length: > 0 } active && active.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) result.Add(active);
            foreach (EnvDTE.Document document in dte.Documents)
            {
                string full;
                try { full = document.FullName; }
                catch (System.Runtime.InteropServices.COMException) { continue; }
                if (!string.IsNullOrEmpty(full) && full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !result.Contains(full, StringComparer.OrdinalIgnoreCase)) result.Add(full);
            }
        }
        catch (System.Runtime.InteropServices.COMException ex) { ActivityLog.LogWarning("SLCopilot", $"Listing open documents failed: {ex.Message}"); }
        return result;
    }

    /// <summary>在编辑器中打开文件并把光标移到 <paramref name="line"/> 行（从 1 开始，0 表示不移动）；行号超出文件时停在文件开头。</summary>
    public async Task OpenFileAtLineAsync(string fullPath, int line, CancellationToken cancellationToken)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        VsShellUtilities.OpenDocument(package, fullPath, Guid.Empty, out _, out _, out var frame, out var view);
        frame?.Show();
        if (view is null || line <= 0) return;
        if (ErrorHandler.Succeeded(view.SetCaretPos(line - 1, 0)))
            view.CenterLines(line - 1, 1);
    }

    /// <summary>
    /// 保存 <paramref name="root"/> 目录下所有有未保存修改的文档（智能体运行前调用，与 VS 编译前自动保存相同）。
    /// 单个文档保存失败时记录到活动日志并跳过（该文件在运行中仍不能被修改）。返回已保存的完整路径。
    /// </summary>
    public async Task<IReadOnlyList<string>> SaveDirtyDocumentsAsync(string root, CancellationToken cancellationToken)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (await package.GetServiceAsync(typeof(EnvDTE.DTE)) is not EnvDTE80.DTE2 dte) return [];
        var prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var saved = new List<string>();
        foreach (EnvDTE.Document document in dte.Documents)
        {
            string full;
            try
            {
                if (document.Saved) continue;
                full = document.FullName;
            }
            catch (System.Runtime.InteropServices.COMException) { continue; }
            if (string.IsNullOrEmpty(full) || !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                document.Save();
                saved.Add(full);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
            {
                ActivityLog.LogWarning("SLCopilot", $"Saving {full} before the agent run failed: {ex.Message}");
            }
        }
        return saved;
    }

    /// <summary>
    /// 目标文件在编辑器中有未保存修改，或打开它的编辑器状态无法确认时抛出 WorkspaceException（Agent 写入前的守卫）。
    /// 文件未打开时直接返回。
    /// </summary>
    public async Task EnsureNotDirtyAsync(string filePath, CancellationToken cancellationToken)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var table = new RunningDocumentTable(package);
        var docData = table.FindDocument(filePath);
        if (docData is null) return;
        if (docData is not IVsPersistDocData persist || ErrorHandler.Failed(persist.IsDocDataDirty(out var dirty)) || dirty != 0)
            throw new Core.WorkspaceException("Target is open with unsaved changes or an unsupported editor. Save or close it before running the agent.");
    }

    /// <summary>
    /// 把 <paramref name="code"/> 插入最近活动的文本编辑器：有选区时替换选区，否则插入到光标处；修改进入编辑器撤销栈。
    /// 没有可用编辑器或编辑被拒绝时返回 null，成功时返回文件名。
    /// </summary>
    public async Task<string?> InsertAtCaretAsync(string code, CancellationToken cancellationToken)
    {
        // 点击工具窗口按钮时编辑器已失去焦点，因此取最近活动的视图。
        var view = await GetActiveViewAsync(cancellationToken, mustHaveFocus: false);
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (view is null || view.IsClosed || view.TextBuffer.CurrentSnapshot.Length > MaxDocumentChars) return null;
        var span = view.Selection.IsEmpty
            ? new SnapshotSpan(view.Caret.Position.BufferPosition, 0)
            : view.Selection.StreamSelectionSpan.SnapshotSpan.TranslateTo(view.TextBuffer.CurrentSnapshot, SpanTrackingMode.EdgeInclusive);
        using var edit = view.TextBuffer.CreateEdit();
        if (!edit.Replace(span.Span, code)) return null;
        edit.Apply();
        return edit.Canceled ? null : Path.GetFileName(GetFilePath(view) ?? "");
    }

    private async Task<IWpfTextView?> GetActiveViewAsync(CancellationToken cancellationToken, bool mustHaveFocus = true)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (await package.GetServiceAsync(typeof(SVsTextManager)) is not IVsTextManager textManager) return null;
        if (ErrorHandler.Failed(textManager.GetActiveView(mustHaveFocus ? 1 : 0, null, out var vsView)) || vsView is null) return null;
        if (await package.GetServiceAsync(typeof(SComponentModel)) is not IComponentModel componentModel) return null;
        return componentModel.GetService<IVsEditorAdaptersFactoryService>().GetWpfTextView(vsView);
    }

    private static string? GetFilePath(ITextView view)
        => view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document) ? document.FilePath : null;
}

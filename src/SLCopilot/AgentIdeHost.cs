using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using SLCopilot.Core;
using Task = System.Threading.Tasks.Task;

namespace SLCopilot;

/// <summary>
/// 智能体使用的 VS 能力：编辑器缓冲区读写、编译解决方案、读取错误列表。
/// 每个方法内部切换到 UI 线程，返回前切回线程池；编辑器写入的检查与修改在同一段 UI 线程代码中完成，中间不让出线程。
/// </summary>
public sealed class AgentIdeHost(AsyncPackage package, Func<string, string, CancellationToken, Task<bool>> confirm) : IIdeHost
{
    /// <summary>取消编译后等待 VS 报告结束的上限。</summary>
    private static readonly TimeSpan BuildStopWait = TimeSpan.FromSeconds(60);
    private readonly JoinableTaskFactory jtf = package.JoinableTaskFactory;

    public async Task<string?> GetUnsavedTextAsync(string fullPath, CancellationToken cancellationToken)
    {
        await jtf.SwitchToMainThreadAsync(cancellationToken);
        try
        {
            var (document, buffer) = await FindOpenDocumentAsync(fullPath);
            return document is not null && buffer is not null && document.IsDirty ? buffer.CurrentSnapshot.GetText() : null;
        }
        finally { await TaskScheduler.Default; }
    }

    public async Task<bool> TryWriteOpenDocumentAsync(string fullPath, string expectedText, string newText, TextFormat format, CancellationToken cancellationToken)
    {
        await jtf.SwitchToMainThreadAsync(cancellationToken);
        try
        {
            var (document, buffer) = await FindOpenDocumentAsync(fullPath);
            if (document is null || buffer is null) return false;
            // 以下检查与修改之间没有 await，用户无法在中间编辑缓冲区。
            if (document.IsDirty) throw new WorkspaceException("Target is open with unsaved changes. Ask the user to save it, then read it again.");
            var encoding = document.Encoding;
            if (encoding.CodePage != format.Encoding.CodePage || (encoding.GetPreamble().Length > 0) != (format.Preamble.Length > 0)) return false;
            var snapshot = buffer.CurrentSnapshot;
            var current = snapshot.GetText();
            if (!string.Equals(current, expectedText, StringComparison.Ordinal))
                throw new WorkspaceException("The editor content differs from the file on disk. Ask the user to reload or save it, then read the file again.");
            cancellationToken.ThrowIfCancellationRequested();

            // 只替换差异部分，保留光标、折叠等编辑器状态；整次替换是一个撤销单元。
            var prefix = 0;
            var max = Math.Min(current.Length, newText.Length);
            while (prefix < max && current[prefix] == newText[prefix]) prefix++;
            var suffix = 0;
            while (suffix < max - prefix && current[current.Length - 1 - suffix] == newText[newText.Length - 1 - suffix]) suffix++;
            using (var edit = buffer.CreateEdit())
            {
                if (!edit.Replace(new Span(prefix, current.Length - prefix - suffix), newText.Substring(prefix, newText.Length - prefix - suffix)))
                    throw new WorkspaceException("The editor rejected the change (the document may be read-only).");
                edit.Apply();
                if (edit.Canceled) throw new WorkspaceException("The editor rejected the change (the document may be read-only).");
            }

            // 已修改缓冲区：保存失败不能抛 WorkspaceException（其含义是“未修改”），改抛 IOException，由调用方标记为未完成写入。
            var docData = new RunningDocumentTable(package).FindDocument(fullPath);
            if (docData is not IVsPersistDocData persist)
                throw new IOException("The change is in the editor but could not be saved. Save the file manually and check the agent changes.");
            if (ErrorHandler.Failed(persist.SaveDocData(VSSAVEFLAGS.VSSAVE_SilentSave, out _, out var canceled)) || canceled != 0)
                throw new IOException("The change is in the editor but saving failed or was canceled. Save the file manually and check the agent changes.");
            return true;
        }
        finally { await TaskScheduler.Default; }
    }

    public async Task<BuildOutcome> BuildSolutionAsync(string? projectPath, CancellationToken cancellationToken)
    {
        var target = projectPath is null ? "整个解决方案" : "项目 " + Path.GetFileName(projectPath) + "（连同它引用的项目）";
        if (!await confirm(projectPath is null ? "编译解决方案" : "编译 " + Path.GetFileName(projectPath),
                $"智能体请求编译{target}，以检查刚才的改动。\n\n编译会执行项目中配置的生成事件（PreBuild / PostBuild，例如复制文件、部署到设备）。如果这些事件在当前环境下不安全，请选择“拒绝”。",
                cancellationToken))
            throw new WorkspaceException("The user declined the build. Do not request another build in this run; use get_errors or ask the user.");

        await jtf.SwitchToMainThreadAsync(cancellationToken);
        IVsHierarchy? hierarchy = null;
        if (projectPath is not null && (hierarchy = await FindProjectAsync(projectPath)) is null)
        {
            await TaskScheduler.Default;
            throw new WorkspaceException("The project is not loaded in the open solution. Build the whole solution instead.");
        }
        if (await package.GetServiceAsync(typeof(SVsSolutionBuildManager)) is not IVsSolutionBuildManager2 manager)
        {
            await TaskScheduler.Default;
            throw new WorkspaceException("The Visual Studio build service is unavailable.");
        }
        if (ErrorHandler.Failed(manager.QueryBuildManagerBusy(out var busy)) || busy != 0)
        {
            await TaskScheduler.Default;
            throw new WorkspaceException("Another build is running. Wait for it to finish, then try again.");
        }
        var events = new BuildEvents();
        // 失败的 HRESULT（如未加载可编译的解决方案、解决方案仍在加载）转换为工具错误，交给模型处理，而不是终止整个运行。
        var advised = manager.AdviseUpdateSolutionEvents(events, out var cookie);
        if (ErrorHandler.Failed(advised))
        {
            await TaskScheduler.Default;
            throw new WorkspaceException($"Visual Studio could not watch the build (HRESULT 0x{advised:X8}). Make sure a buildable solution is loaded.");
        }
        try
        {
            var started = hierarchy is null
                ? manager.StartSimpleUpdateSolutionConfiguration((uint)VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_BUILD, 0, 0)
                : manager.StartSimpleUpdateProjectConfiguration(hierarchy, null, null, (uint)VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_BUILD, 0, 0);
            if (ErrorHandler.Failed(started))
                throw new WorkspaceException($"Visual Studio could not start the build (HRESULT 0x{started:X8}). Make sure a buildable solution is loaded and not still loading.");
            try
            {
                return await events.Completion.WithCancellation(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 区分“请求取消”与“编译已结束”：先请求 VS 取消，再等待其报告结束，超时则如实报告仍在停止。
                await jtf.SwitchToMainThreadAsync();
                manager.CancelUpdateSolutionConfiguration();
                using var wait = new CancellationTokenSource(BuildStopWait);
                try { await events.Completion.WithCancellation(wait.Token); }
                catch (OperationCanceledException)
                {
                    ActivityLog.LogWarning("SLCopilot", "The build did not report completion within 60 seconds after cancellation.");
                }
                throw;
            }
        }
        finally
        {
            await jtf.SwitchToMainThreadAsync();
            manager.UnadviseUpdateSolutionEvents(cookie);
            await TaskScheduler.Default;
        }
    }

    /// <summary>按项目文件路径在打开的解决方案中查找项目（含解决方案文件夹中的项目）；找不到时返回 null。调用后位于 UI 线程。</summary>
    private async Task<IVsHierarchy?> FindProjectAsync(string projectPath)
    {
        await jtf.SwitchToMainThreadAsync();
        if (await package.GetServiceAsync(typeof(EnvDTE.DTE)) is not DTE2 dte || await package.GetServiceAsync(typeof(SVsSolution)) is not IVsSolution solution) return null;
        await jtf.SwitchToMainThreadAsync();
        string full;
        try { full = Path.GetFullPath(projectPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
        string? uniqueName = null;
        void Visit(EnvDTE.Project project)
        {
            if (uniqueName is not null) return;
            string? file = null;
            try { file = project.FullName is { Length: > 0 } name ? Path.GetFullPath(name) : null; }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or NotSupportedException or NotImplementedException or IOException) { }
            if (file is not null && string.Equals(file, full, StringComparison.OrdinalIgnoreCase)) { uniqueName = project.UniqueName; return; }
            if (project.ProjectItems is null) return;
            foreach (EnvDTE.ProjectItem item in project.ProjectItems)
                if (item.SubProject is { } nested) Visit(nested);
        }
        try { foreach (EnvDTE.Project project in dte.Solution.Projects) Visit(project); }
        // 第三方项目系统可能抛出 COM 之外的异常（如 NotImplementedException）：都按“找不到项目”处理，不让异常带着 UI 线程离开。
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or NotImplementedException or NotSupportedException or ArgumentException or InvalidOperationException)
        { ActivityLog.LogWarning("SLCopilot", $"Enumerating projects failed: {ex.Message}"); }
        if (uniqueName is null) return null;
        return ErrorHandler.Succeeded(solution.GetProjectOfUniqueName(uniqueName, out var hierarchy)) ? hierarchy : null;
    }

    public async Task<IReadOnlyList<IdeDiagnostic>> GetDiagnosticsAsync(int maxItems, CancellationToken cancellationToken)
    {
        await jtf.SwitchToMainThreadAsync(cancellationToken);
        try
        {
            if (await package.GetServiceAsync(typeof(EnvDTE.DTE)) is not DTE2 dte)
                throw new WorkspaceException("The Visual Studio Error List is unavailable.");
            var errors = new List<IdeDiagnostic>();
            var warnings = new List<IdeDiagnostic>();
            try
            {
                var items = dte.ToolWindows.ErrorList.ErrorItems;
                for (var i = 1; i <= items.Count && errors.Count < maxItems; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = items.Item(i);
                    var target = item.ErrorLevel switch
                    {
                        EnvDTE80.vsBuildErrorLevel.vsBuildErrorLevelHigh => errors,
                        EnvDTE80.vsBuildErrorLevel.vsBuildErrorLevelMedium => warnings,
                        _ => null,
                    };
                    target?.Add(new(target == errors ? "error" : "warning", item.FileName ?? "", item.Line, item.Column, item.Project ?? "", item.Description ?? ""));
                }
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                ActivityLog.LogWarning("SLCopilot", $"Reading the Error List failed: {ex.Message}");
                throw new WorkspaceException("Reading the Visual Studio Error List failed.");
            }
            return errors.Concat(warnings).Take(maxItems).ToList();
        }
        finally { await TaskScheduler.Default; }
    }

    public async Task<IReadOnlyList<CodeSymbol>> FindSymbolsAsync(string name, int maxItems, CancellationToken cancellationToken)
        => await RoslynNavigator.FindSymbolsAsync(await SolutionAsync(cancellationToken), name, maxItems, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<CodeSymbol>> FindReferencesAsync(string name, int maxItems, CancellationToken cancellationToken)
        => await RoslynNavigator.FindReferencesAsync(await SolutionAsync(cancellationToken), name, maxItems, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<CodeSymbol>> GetOutlineAsync(string fullPath, CancellationToken cancellationToken)
        => await RoslynNavigator.GetOutlineAsync(await SolutionAsync(cancellationToken), fullPath, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<CodeSymbol>> GetHierarchyAsync(string name, int maxItems, CancellationToken cancellationToken)
        => await RoslynNavigator.GetHierarchyAsync(await SolutionAsync(cancellationToken), name, maxItems, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ProjectNode>> GetProjectGraphAsync(CancellationToken cancellationToken)
        => RoslynNavigator.GetProjectGraph(await SolutionAsync(cancellationToken));

    public async Task<IReadOnlyList<IdeDiagnostic>?> CheckFileAsync(string fullPath, string? text, int maxItems, CancellationToken cancellationToken)
        => await RoslynNavigator.CheckFileAsync(await SolutionAsync(cancellationToken), fullPath, text, maxItems, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<SymbolSource>> ReadSymbolAsync(string name, int maxLines, CancellationToken cancellationToken)
        => await RoslynNavigator.ReadSymbolAsync(await SolutionAsync(cancellationToken), name, maxLines, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<CodeSymbol>> GetCallersAsync(string name, int maxItems, CancellationToken cancellationToken)
        => await RoslynNavigator.GetCallersAsync(await SolutionAsync(cancellationToken), name, maxItems, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<CodeSymbol>> GetCalleesAsync(string name, int maxItems, CancellationToken cancellationToken)
        => await RoslynNavigator.GetCalleesAsync(await SolutionAsync(cancellationToken), name, maxItems, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<CodeSymbol>> GetTypeDependenciesAsync(string name, int maxItems, CancellationToken cancellationToken)
        => await RoslynNavigator.GetTypeDependenciesAsync(await SolutionAsync(cancellationToken), name, maxItems, cancellationToken).ConfigureAwait(false);

    /// <summary>取 Roslyn 当前解决方案快照（在 UI 线程获取服务），返回后位于线程池。</summary>
    private async Task<Microsoft.CodeAnalysis.Solution> SolutionAsync(CancellationToken cancellationToken)
    {
        await jtf.SwitchToMainThreadAsync(cancellationToken);
        try
        {
            if (await package.GetServiceAsync(typeof(SComponentModel)) is not IComponentModel componentModel)
                throw new WorkspaceException("The Roslyn workspace is unavailable.");
            await jtf.SwitchToMainThreadAsync(cancellationToken);
            return componentModel.GetService<Microsoft.VisualStudio.LanguageServices.VisualStudioWorkspace>().CurrentSolution;
        }
        finally { await TaskScheduler.Default; }
    }

    /// <summary>在运行文档表中查找以文本编辑器打开的文档；未打开或不是文本文档时返回 (null, null)。调用后位于 UI 线程。</summary>
    private async Task<(ITextDocument? Document, ITextBuffer? Buffer)> FindOpenDocumentAsync(string fullPath)
    {
        await jtf.SwitchToMainThreadAsync();
        var docData = new RunningDocumentTable(package).FindDocument(fullPath);
        IVsTextBuffer? vsBuffer = docData as IVsTextBuffer;
        if (vsBuffer is null && docData is IVsTextBufferProvider provider && ErrorHandler.Succeeded(provider.GetTextBuffer(out var lines)))
            vsBuffer = lines;
        if (vsBuffer is null) return (null, null);
        if (await package.GetServiceAsync(typeof(SComponentModel)) is not IComponentModel componentModel) return (null, null);
        await jtf.SwitchToMainThreadAsync();
        var buffer = componentModel.GetService<IVsEditorAdaptersFactoryService>().GetDocumentBuffer(vsBuffer);
        if (buffer is null || !buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document)) return (null, null);
        return string.Equals(Path.GetFullPath(document.FilePath), Path.GetFullPath(fullPath), StringComparison.OrdinalIgnoreCase) ? (document, buffer) : (null, null);
    }

    /// <summary>把解决方案编译事件转换为任务。事件在 UI 线程触发。</summary>
    private sealed class BuildEvents : IVsUpdateSolutionEvents
    {
        private readonly TaskCompletionSource<BuildOutcome> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<BuildOutcome> Completion => completion.Task;

        public int UpdateSolution_Begin(ref int pfCancelUpdate) => VSConstants.S_OK;
        public int UpdateSolution_StartUpdate(ref int pfCancelUpdate) => VSConstants.S_OK;
        public int OnActiveProjectCfgChange(IVsHierarchy pIVsHierarchy) => VSConstants.S_OK;

        public int UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand)
        {
            completion.TrySetResult(new(fSucceeded != 0 && fCancelCommand == 0, fCancelCommand != 0));
            return VSConstants.S_OK;
        }

        public int UpdateSolution_Cancel()
        {
            completion.TrySetResult(new(false, true));
            return VSConstants.S_OK;
        }
    }
}

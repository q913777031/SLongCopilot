using System.Net.Sockets;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using SLCopilot.Core;

namespace SLCopilot;

/// <summary>
/// Claude CLI 模式的桥接：保存端口与写入权限，管理令牌（Windows 凭据管理器），启动或停止 <see cref="McpBridgeServer"/>，并给出注册与启动 Claude CLI 的命令。
/// 是否处于 Claude CLI 模式由 <see cref="ChatViewModel"/> 通过 <see cref="EnableAsync"/> / <see cref="DisableAsync"/> 切换（与内置智能体互斥）；
/// 启用、停用与重启串行执行，停用在 Claude CLI 正在执行的调用全部结束后才返回。属性与命令只在 UI 线程使用；
/// 启动失败（端口被占用、凭据不可用）时服务保持停止并在 <see cref="Status"/> 中说明。
/// </summary>
public sealed class ClaudeBridge : ObservableObject, IDisposable
{
    private const string TokenTarget = "SLCopilot:bridge-token";
    /// <summary>Claude CLI 中会读写磁盘的内置工具：它们看到的是加密文件，必须禁用，只用桥接工具。</summary>
    private const string DisallowedTools = "Read Write Edit MultiEdit NotebookEdit Grep Glob Bash";

    private readonly ModelProfileManager profiles;
    private readonly UiQueue ui;
    private readonly JoinableTaskFactory jtf;
    private readonly Func<CancellationToken, Task<string?>> resolveRoot;
    private readonly Func<string, bool, WorkspaceTools> createWorkspace;
    // 启用、停用与重启互斥：重启时旧服务的调用全部结束后才启动新服务，两个服务的写入不会重叠。
    private readonly SemaphoreSlim transitions = new(1, 1);
    private McpBridgeServer? server;
    private string portText, status = "未启用：在输入区左下角选择“Claude CLI”模式后启动。", registerCommand = "";

    /// <param name="resolveRoot">当前解决方案目录；可在任意线程调用。</param>
    /// <param name="createWorkspace">为一个 Claude CLI 会话创建工作区工具（审批、IDE、命令执行与智能体相同）。</param>
    public ClaudeBridge(JoinableTaskFactory jtf, ModelProfileManager profiles, UiQueue ui, Func<CancellationToken, Task<string?>> resolveRoot,
        Func<string, bool, WorkspaceTools> createWorkspace)
    {
        this.jtf = jtf;
        this.profiles = profiles;
        this.ui = ui;
        this.resolveRoot = resolveRoot;
        this.createWorkspace = createWorkspace;
        portText = profiles.BridgePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CopyRegisterCommand = new RelayCommand(() => Copy(RegisterCommand, "已复制注册命令（含令牌）。在命令行执行一次即可。"));
        CopyLaunchCommand = new RelayCommand(() => Copy(LaunchCommand, "已复制启动命令。在解决方案目录中执行。"));
        RegenerateTokenCommand = new AsyncCommand(jtf, "RegenerateBridgeToken", RegenerateTokenAsync);
    }

    public ICommand CopyRegisterCommand { get; }
    public ICommand CopyLaunchCommand { get; }
    public ICommand RegenerateTokenCommand { get; }

    /// <summary>上次退出 VS 时是否处于 Claude CLI 模式（profiles.json）；启动时据此恢复。</summary>
    public bool Enabled => profiles.BridgeEnabled;

    /// <summary>桥接是否正在监听。</summary>
    public bool IsRunning => server is not null;

    /// <summary>是否提供写入、删除、移动、编译、测试与命令工具；Claude CLI 模式中修改时重启桥接，已连接的 Claude CLI 需执行 /mcp 重新连接。</summary>
    public bool AllowWrites
    {
        get => profiles.BridgeAllowWrites;
        set
        {
            if (value == profiles.BridgeAllowWrites) return;
            if (Save(profiles.BridgeEnabled, profiles.BridgePort, value) && IsRunning) jtf.RunAsync(RestartAsync).FileAndForget("SLCopilot/bridgeRestart");
            RaisePropertyChanged(nameof(AllowWrites));
        }
    }

    /// <summary>端口文本；失去焦点时校验并保存，合法且处于 Claude CLI 模式时重启桥接。</summary>
    public string PortText
    {
        get => portText;
        set
        {
            var trimmed = (value ?? "").Trim();
            if (!int.TryParse(trimmed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535)
            {
                Status = "端口必须是 1024–65535 之间的整数，未保存。";
                RaisePropertyChanged(nameof(PortText));
                return;
            }
            if (!SetProperty(ref portText, trimmed) || port == profiles.BridgePort) return;
            if (Save(profiles.BridgeEnabled, port, profiles.BridgeAllowWrites) && IsRunning) jtf.RunAsync(RestartAsync).FileAndForget("SLCopilot/bridgeRestart");
        }
    }

    public string Status { get => status; private set => SetProperty(ref status, value); }

    /// <summary>注册命令（含令牌，等同密码）；桥接未运行时为空。</summary>
    public string RegisterCommand { get => registerCommand; private set => SetProperty(ref registerCommand, value); }

    /// <summary>启动 Claude CLI 并禁用会读写磁盘的内置工具。</summary>
    public string LaunchCommand => $"claude --disallowedTools {DisallowedTools}";

    /// <summary>进入 Claude CLI 模式：启动桥接并记住该模式。返回 false 表示未启动（原因在 <see cref="Status"/>），模式不变。只在 UI 线程调用。</summary>
    public async Task<bool> EnableAsync()
    {
        await jtf.SwitchToMainThreadAsync();
        await transitions.WaitAsync();
        try
        {
            await jtf.SwitchToMainThreadAsync();
            if (!Start()) return false;
            Save(true, profiles.BridgePort, profiles.BridgeAllowWrites);
            return true;
        }
        finally { transitions.Release(); }
    }

    /// <summary>
    /// 离开 Claude CLI 模式：停止监听、取消 Claude CLI 正在执行的调用，并等待它们全部结束后返回（不设超时：未结束时不能确认工作区状态）。
    /// 返回后桥接不再执行任何工具调用。只在 UI 线程调用。
    /// </summary>
    public async Task DisableAsync()
    {
        await jtf.SwitchToMainThreadAsync();
        await transitions.WaitAsync();
        try
        {
            await jtf.SwitchToMainThreadAsync();
            await StopAsync();
            Save(false, profiles.BridgePort, profiles.BridgeAllowWrites);
            RegisterCommand = "";
            Status = "已停止：内置智能体可用。";
        }
        finally { transitions.Release(); }
    }

    /// <summary>VS 启动时按上次的模式恢复：上次处于 Claude CLI 模式时启动桥接。返回桥接是否在运行。只在 UI 线程调用。</summary>
    public bool StartIfEnabled()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return profiles.BridgeEnabled && Start();
    }

    /// <summary>VS 关闭时调用：停止监听并取消进行中的调用，不等待（进程即将退出）。</summary>
    public void Dispose()
    {
        server?.Dispose();
        server = null;
    }

    private async Task RestartAsync()
    {
        await jtf.SwitchToMainThreadAsync();
        await transitions.WaitAsync();
        try
        {
            await jtf.SwitchToMainThreadAsync();
            if (!IsRunning) return;
            await StopAsync();
            Start();
        }
        finally { transitions.Release(); }
    }

    /// <summary>停止当前服务并等待其调用全部结束；等待期间在状态中显示剩余调用数。</summary>
    private async Task StopAsync()
    {
        if (server is not { } running) return;
        server = null;
        var stopped = running.StopAsync();
        if (!stopped.IsCompleted)
        {
            Status = $"正在停止：等待 Claude CLI 的 {running.ActiveCalls} 个操作结束（已请求取消；正在写入的文件会写完）…";
            RunLog.Info("bridge", "bridge_draining", ("calls", running.ActiveCalls));
        }
        await stopped;
        await jtf.SwitchToMainThreadAsync();
    }

    /// <summary>按当前设置启动服务；失败时保持停止并说明原因。调用方持有 <see cref="transitions"/> 或处于构造阶段。</summary>
    private bool Start()
    {
        string token;
        try { token = ReadOrCreateToken(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException)
        {
            RunLog.Error("bridge", "bridge_token_failed", ex);
            Status = "无法读取或保存桥接令牌（Windows 凭据管理器）：" + ex.Message;
            return false;
        }
        var options = new McpBridgeOptions(profiles.BridgePort, token, profiles.BridgeAllowWrites);
        var started = new McpBridgeServer(options, resolveRoot, createWorkspace);
        started.SessionsChanged += () => ui.Post(UpdateStatus);
        try { started.Start(); }
        catch (SocketException ex)
        {
            started.Dispose();
            RunLog.Warn("bridge", "bridge_start_failed", ("port", options.Port), ("code", ex.SocketErrorCode), ("message", ex.Message));
            Status = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"端口 {options.Port} 已被占用（可能另一个 VS 实例处于 Claude CLI 模式），桥接未启动。请在“设置”中换一个端口，并按新端口重新注册。"
                : $"桥接未启动：{ex.Message}";
            RegisterCommand = "";
            return false;
        }
        server = started;
        RegisterCommand = $"claude mcp add --transport http --scope user {McpBridgeServer.ServerName} http://127.0.0.1:{options.Port}{McpBridgeServer.EndpointPath} --header \"Authorization: Bearer {token}\"";
        UpdateStatus();
        return true;
    }

    private void UpdateStatus()
    {
        if (server is not { } running) return;
        Status = $"运行中：127.0.0.1:{running.Port}（{(running.AllowWrites ? "读写" : "只读")}），已连接 {running.SessionCount} 个 Claude CLI 会话。";
    }

    private bool Save(bool enabled, int port, bool allowWrites)
    {
        try
        {
            profiles.SetBridge(enabled, port, allowWrites);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ActivityLog.LogWarning("SLCopilot", $"Saving the bridge settings failed: {ex.Message}");
            Status = "无法保存桥接设置：" + ex.Message;
            return false;
        }
    }

    private static string ReadOrCreateToken()
    {
        if (CredentialStore.Read(TokenTarget) is { } stored)
        {
            try
            {
                new McpBridgeOptions(McpBridgeOptions.DefaultPort, stored, false).Validate();
                return stored;
            }
            catch (ArgumentException)
            {
                RunLog.Warn("bridge", "bridge_token_invalid");
            }
        }
        var token = McpBridgeOptions.NewToken();
        CredentialStore.Write(TokenTarget, token);
        return token;
    }

    /// <summary>换新令牌：旧令牌立即失效，已注册的 Claude CLI 需用新的注册命令重新注册；Claude CLI 模式中会重启桥接。</summary>
    private async Task RegenerateTokenAsync()
    {
        await jtf.SwitchToMainThreadAsync();
        try { CredentialStore.Write(TokenTarget, McpBridgeOptions.NewToken()); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException)
        {
            RunLog.Error("bridge", "bridge_token_failed", ex);
            Status = "无法保存新令牌：" + ex.Message;
            return;
        }
        RunLog.Info("bridge", "bridge_token_regenerated");
        await RestartAsync();
        Status += " 令牌已更换：请先执行 claude mcp remove slcopilot -s user，再执行新的注册命令。";
    }

    private void Copy(string text, string done)
    {
        if (text.Length == 0)
        {
            Status = "请先启用桥接。";
            return;
        }
        try
        {
            System.Windows.Clipboard.SetText(text);
            Status = done;
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Status = "复制失败（剪贴板被占用），请重试：" + ex.Message;
        }
    }
}

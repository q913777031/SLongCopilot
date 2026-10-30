namespace SLCopilot;

/// <summary>本机数据目录 %LOCALAPPDATA%\SLCopilot 下的固定位置。<see cref="EnsureLogging"/> 可重复调用。</summary>
internal static class SLCopilotPaths
{
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SLCopilot");
    public static readonly string Logs = Path.Combine(Root, "logs");

    /// <summary>启用运行日志。包与行内补全组件（MEF，可能先于包加载）都会调用。</summary>
    public static void EnsureLogging() => Core.RunLog.Directory ??= Logs;
}

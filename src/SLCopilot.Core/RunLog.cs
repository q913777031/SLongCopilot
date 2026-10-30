using System.Globalization;
using System.Text;

namespace SLCopilot.Core;

/// <summary>
/// 运行日志：每行一条事件，含时间、级别、运行编号（run）与键值字段，用于现场排障时关联同一次运行。
/// 约定只记录元数据（工具名、相对路径、哈希、状态码、耗时、token 数），不记录密钥、提示词或代码正文；字段值压成单行并截断到 300 字符。
/// 文件为 {Directory}/slcopilot-yyyyMMdd.log，单个超过 5 MB 时换下一个序号；保留最近 14 天、最多 30 个文件。
/// 线程安全。写入失败不抛出（日志不能影响主流程），失败原因保存在 <see cref="LastError"/>，60 秒内不再尝试。
/// </summary>
public static class RunLog
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int KeepDays = 14, MaxFiles = 30;
    private static readonly object Gate = new();
    private static DateTime retryAfter, prunedOn;

    /// <summary>日志目录；为 null 时不写入（测试与未初始化时）。</summary>
    public static string? Directory { get; set; }

    /// <summary>最近一次写入失败的原因；成功写入后清空。</summary>
    public static string? LastError { get; private set; }

    public static string NewRunId() => Guid.NewGuid().ToString("N").Substring(0, 12);

    public static void Info(string run, string evt, params (string Key, object? Value)[] fields) => Write("INFO", run, evt, fields);
    public static void Warn(string run, string evt, params (string Key, object? Value)[] fields) => Write("WARN", run, evt, fields);

    /// <summary>记录异常：类型、消息与调用栈（不含任何请求内容）。</summary>
    public static void Error(string run, string evt, Exception ex, params (string Key, object? Value)[] fields)
        => Write("ERROR", run, evt, [.. fields, ("error", ex.GetType().Name), ("message", ex.Message)], ex.StackTrace);

    public static void Write(string level, string run, string evt, (string Key, object? Value)[] fields, string? stack = null)
    {
        var directory = Directory;
        if (directory is null) return;
        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture))
            .Append(' ').Append(level).Append(" run=").Append(Clean(run)).Append(" event=").Append(Clean(evt));
        foreach (var (key, value) in fields)
            line.Append(' ').Append(key).Append('=').Append(Format(value));
        if (!string.IsNullOrEmpty(stack)) line.Append(" stack=").Append(Clean(stack!.Replace("\r", "").Replace("\n", " | "), 4000));
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (now < retryAfter) return;
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                if (prunedOn != now.Date) { Prune(directory); prunedOn = now.Date; }
                var path = CurrentFile(directory);
                using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    writer.WriteLine(line.ToString());
                LastError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                LastError = ex.Message;
                retryAfter = now.AddSeconds(60);
            }
        }
    }

    private static string CurrentFile(string directory)
    {
        var stem = "slcopilot-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        for (var index = 0; ; index++)
        {
            var path = Path.Combine(directory, index == 0 ? stem + ".log" : $"{stem}-{index}.log");
            if (!File.Exists(path) || new FileInfo(path).Length < MaxFileBytes) return path;
        }
    }

    private static void Prune(string directory)
    {
        var files = new DirectoryInfo(directory).GetFiles("slcopilot-*.log").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        foreach (var file in files.Where((f, i) => i >= MaxFiles || f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-KeepDays)))
        {
            try { file.Delete(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Format(object? value) => value switch
    {
        null => "-",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Quote(Clean(value.ToString() ?? "")),
    };

    private static string Quote(string text) => text.Length == 0 || text.IndexOfAny([' ', '"', '=']) >= 0 ? "\"" + text.Replace("\"", "'") + "\"" : text;

    private static string Clean(string text, int max = 300)
    {
        var single = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return single.Length > max ? single.Substring(0, max) + "…" : single;
    }
}

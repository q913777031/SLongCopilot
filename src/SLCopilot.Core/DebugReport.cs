using System.Text;

namespace SLCopilot.Core;

public sealed record DebugFrame(string Function, string? File, int Line, string Module);

public sealed record DebugValue(string Name, string Type, string Value);

/// <summary>调试中断时的快照。没有当前异常（如断点中断）时异常字段为 null。</summary>
public sealed record DebugSnapshot(string? ExceptionType, string? ExceptionMessage, string? ExceptionStackTrace, string? InnerException,
    IReadOnlyList<DebugFrame> Frames, IReadOnlyList<DebugValue> Locals);

/// <summary>把调试快照整理成发送给模型的文字：调用栈与局部变量限量，长值截断；工作区内的路径显示为相对路径。</summary>
public static class DebugReport
{
    public const int MaxFrames = 30, MaxLocals = 40, MaxValueChars = 300, MaxStackTraceChars = 6000;

    public static string Format(DebugSnapshot snapshot, string? root)
    {
        var text = new StringBuilder();
        if (snapshot.ExceptionType is not null)
        {
            text.Append("异常：").Append(snapshot.ExceptionType);
            if (Unquote(snapshot.ExceptionMessage) is { Length: > 0 } message) text.Append("\n消息：").Append(Shorten(message, 2000));
            if (snapshot.InnerException is { Length: > 0 } inner && inner != "null") text.Append("\n内部异常：").Append(Shorten(inner, 1000));
            if (Unquote(snapshot.ExceptionStackTrace) is { Length: > 0 } trace)
                text.Append("\n异常堆栈：\n").Append(Shorten(Relative(trace, root), MaxStackTraceChars));
        }
        else text.Append("中断原因：断点或单步（没有当前异常）");
        if (snapshot.Frames.Count > 0)
        {
            text.Append("\n\n调用栈（当前线程，最上面为当前帧）：");
            foreach (var frame in snapshot.Frames.Take(MaxFrames))
            {
                text.Append("\n- ").Append(frame.Function);
                if (!string.IsNullOrEmpty(frame.File)) text.Append("  @ ").Append(Relative(frame.File!, root)).Append(':').Append(frame.Line);
                else if (frame.Module.Length > 0) text.Append("  [").Append(frame.Module).Append(']');
            }
            if (snapshot.Frames.Count > MaxFrames) text.Append("\n- …（更多帧已省略）");
        }
        if (snapshot.Locals.Count > 0)
        {
            text.Append("\n\n当前帧的局部变量（发送前可删改敏感值）：");
            foreach (var local in snapshot.Locals.Take(MaxLocals))
                text.Append("\n- ").Append(local.Name).Append(" (").Append(local.Type).Append(") = ").Append(Shorten(local.Value.Replace("\r", "").Replace('\n', ' '), MaxValueChars));
            if (snapshot.Locals.Count > MaxLocals) text.Append("\n- …（更多局部变量已省略）");
        }
        return text.ToString();
    }

    /// <summary>调试器返回的字符串值带引号与转义（如 "a\r\nb"）；去掉引号并还原常见转义。不是带引号的字符串时原样返回。</summary>
    public static string? Unquote(string? value)
    {
        if (value is null || value == "null") return null;
        if (value.Length < 2 || value[0] != '"' || value[value.Length - 1] != '"') return value;
        return value.Substring(1, value.Length - 2).Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\"", "\"").Replace("\\\\", "\\");
    }

    private static string Relative(string text, string? root)
    {
        if (string.IsNullOrEmpty(root)) return text;
        var prefix = root!.TrimEnd('\\', '/') + "\\";
        return text.Replace(prefix, "").Replace(prefix.Replace('\\', '/'), "");
    }

    private static string Shorten(string value, int max) => value.Length <= max ? value : value.Substring(0, max) + "…";
}

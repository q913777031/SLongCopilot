using System.Net;
using System.Security.Cryptography;

namespace SLCopilot.Core;

/// <summary>
/// net472 与 net8.0 共用的基础 API。凡涉及路径边界与写盘语义的实现两个目标共用同一份代码，以便在任一平台的测试中覆盖。
/// </summary>
internal static class Compat
{
    public static bool IsWindows =>
#if NETFRAMEWORK
        true;
#else
        OperatingSystem.IsWindows();
#endif

    public static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
    }

    /// <summary>判断是否为带盘符或 UNC 的完整路径（Unix 上为以 / 开头）；不接受 "C:foo" 或 "\foo" 这类相对于当前盘的写法。</summary>
    public static bool IsPathFullyQualified(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (!IsWindows) return path[0] == '/';
        if (path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1])) return true;
        return path.Length >= 3 && IsAsciiLetter(path[0]) && path[1] == ':' && IsSeparator(path[2]);
    }

    /// <summary>去掉末尾目录分隔符，但保留根（如 "C:\" 或 "/"）。</summary>
    public static string TrimEndingDirectorySeparator(string path)
    {
        var root = Path.GetPathRoot(path) ?? "";
        var end = path.Length;
        while (end > root.Length && IsSeparator(path[end - 1])) end--;
        return path.Substring(0, end);
    }

    /// <summary>
    /// 返回 <paramref name="full"/> 相对 <paramref name="root"/> 的路径（使用平台分隔符）。
    /// 只接受位于根目录内（含根本身）的路径，否则抛出 WorkspaceException；不做 "..\" 形式的回溯计算。
    /// </summary>
    public static string RelativeTo(string root, string full)
    {
        var comparison = IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        root = TrimEndingDirectorySeparator(root);
        if (string.Equals(full, root, comparison)) return ".";
        var prefix = root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, comparison)) throw new WorkspaceException("Path is outside the workspace.");
        return full.Substring(prefix.Length);
    }

    /// <summary>用临时文件替换目标：目标存在时原子替换（NTFS 上为 ReplaceFile），否则直接改名。</summary>
    public static void MoveOverwrite(string source, string destination)
    {
#if NETFRAMEWORK
        if (File.Exists(destination)) File.Replace(source, destination, null, ignoreMetadataErrors: true);
        else File.Move(source, destination);
#else
        File.Move(source, destination, overwrite: true);
#endif
    }

    public static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken token)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        var bytes = new byte[file.Length];
        await ReadExactlyAsync(file, bytes, token).ConfigureAwait(false);
        return bytes;
    }

    public static async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken token)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
        await file.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
    }

    public static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    public static Task<Stream> ReadAsStreamAsync(HttpContent content, CancellationToken token) =>
#if NETFRAMEWORK
        content.ReadAsStreamAsync();
#else
        content.ReadAsStreamAsync(token);
#endif

    /// <summary>读取完整响应文本。net472 的读取不响应取消令牌：取消时释放内容以中断读取。</summary>
    public static async Task<string> ReadAsStringAsync(HttpContent content, CancellationToken token)
    {
        using var abort = token.Register(content.Dispose);
        try
        {
            using var stream = await ReadAsStreamAsync(content, token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (IOException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';
    private static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}

/// <summary>服务商返回非成功状态码。只携带状态码，不包含响应正文（可能含敏感信息）。</summary>
public sealed class ProviderHttpException(HttpStatusCode status, string message) : HttpRequestException(message)
{
    public HttpStatusCode Status { get; } = status;
}

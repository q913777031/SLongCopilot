#if NETFRAMEWORK
using System.Text;

namespace SLCopilot.Core.Tests;

// 仅 net472 测试使用：同名类型优先于 System.IO.File 被解析，测试代码无需按目标框架分支。
internal static class File
{
    public static bool Exists(string path) => System.IO.File.Exists(path);
    public static void Delete(string path) => System.IO.File.Delete(path);
    public static string ReadAllText(string path) => System.IO.File.ReadAllText(path);
    public static Task<string> ReadAllTextAsync(string path, CancellationToken token = default) => Task.FromResult(System.IO.File.ReadAllText(path));
    public static Task<byte[]> ReadAllBytesAsync(string path, CancellationToken token = default) => Task.FromResult(System.IO.File.ReadAllBytes(path));
    public static Task WriteAllTextAsync(string path, string text, CancellationToken token = default)
    { System.IO.File.WriteAllText(path, text, new UTF8Encoding(false)); return Task.CompletedTask; }
    public static Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken token = default)
    { System.IO.File.WriteAllBytes(path, bytes); return Task.CompletedTask; }
}

internal static class NetFrameworkExtensions
{
    public static Task CancelAsync(this CancellationTokenSource source) { source.Cancel(); return Task.CompletedTask; }
    public static Task<string> ReadAsStringAsync(this HttpContent content, CancellationToken token) => content.ReadAsStringAsync();
}
#endif

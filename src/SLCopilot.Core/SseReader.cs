using System.Runtime.CompilerServices;
using System.Text;

namespace SLCopilot.Core;

public static class SseReader
{
    // Bound each event before parsing JSON, including peers that never send a newline.
    public static async IAsyncEnumerable<string> ReadAsync(TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default, int maxEventChars = 65_536)
    {
        var line = new StringBuilder();
        var data = new StringBuilder();
        var buffer = new char[2048];
        while (true)
        {
            var count = await ReadChunkAsync(reader, buffer, cancellationToken);
            if (count == 0) break;
            for (var i = 0; i < count; i++)
            {
                var c = buffer[i];
                if (c != '\n')
                {
                    line.Append(c);
                    if (line.Length + data.Length > maxEventChars) throw new IOException("SSE event is too large.");
                    continue;
                }
                var value = line.ToString().TrimEnd('\r');
                line.Clear();
                if (value.Length == 0)
                {
                    if (data.Length > 0) { yield return data.ToString().TrimEnd('\n'); data.Clear(); }
                }
                else if (value.StartsWith("data:", StringComparison.Ordinal))
                {
                    var part = value[5..];
                    if (part.StartsWith(" ", StringComparison.Ordinal)) part = part[1..];
                    data.Append(part).Append('\n');
                }
            }
        }
        // An incomplete final event is not a successful completion.
    }

    // net472 的 TextReader.ReadAsync 不接受取消令牌：调用方取消时会释放底层流，此处把由此产生的异常统一转换为取消。
    private static async Task<int> ReadChunkAsync(TextReader reader, char[] buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
#if NETFRAMEWORK
            return await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
#else
            return await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
#endif
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }
}

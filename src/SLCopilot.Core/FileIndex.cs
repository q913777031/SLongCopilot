namespace SLCopilot.Core;

/// <summary>
/// 工作区文件列表与模糊匹配，供输入框中的 @ / #file: 补全使用。只列文件名，不读取内容；跳过隐藏目录与 bin、obj 等生成目录以及常见二进制文件。
/// </summary>
public static class FileIndex
{
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", "packages", "TestResults", "artifacts" };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".dll", ".exe", ".pdb", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".zip", ".7z", ".nupkg", ".snk", ".pfx", ".db", ".suo", ".user", ".cache" };

    /// <summary>列出工作区中的文件（相对路径，分隔符为 /），最多 <paramref name="maxFiles"/> 个；无法访问的目录跳过。</summary>
    public static IReadOnlyList<string> Build(string root, int maxFiles = 20_000, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(root);
        var prefix = full.TrimEnd('\\', '/').Length + 1;
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(full);
        while (pending.Count > 0 && result.Count < maxFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    if (BinaryExtensions.Contains(Path.GetExtension(file))) continue;
                    result.Add(file.Substring(prefix).Replace('\\', '/'));
                    if (result.Count >= maxFiles) break;
                }
                foreach (var child in Directory.EnumerateDirectories(folder))
                {
                    var name = Path.GetFileName(child);
                    if (name.StartsWith(".", StringComparison.Ordinal) || SkippedFolders.Contains(name)) continue;
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                    pending.Push(child);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>
    /// 按查询排序匹配的文件：文件名以查询开头 &gt; 文件名包含 &gt; 路径包含 &gt; 按顺序包含查询的全部字符；同级按路径长度。不区分大小写，查询中的 \ 视为 /。
    /// </summary>
    public static IReadOnlyList<string> Match(IReadOnlyList<string> files, string query, int max = 8)
    {
        query = query.Trim().Replace('\\', '/');
        if (query.Length == 0) return [];
        var scored = new List<(int Rank, string Path)>();
        foreach (var path in files)
        {
            var name = path.Substring(path.LastIndexOf('/') + 1);
            var rank = name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0
                : name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ? 1
                : path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ? 2
                : IsSubsequence(query, path) ? 3 : -1;
            if (rank >= 0) scored.Add((rank, path));
        }
        return scored.OrderBy(s => s.Rank).ThenBy(s => s.Path.Length).ThenBy(s => s.Path, StringComparer.OrdinalIgnoreCase).Take(max).Select(s => s.Path).ToList();
    }

    private static bool IsSubsequence(string query, string text)
    {
        var position = 0;
        foreach (var c in text)
            if (position < query.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(query[position])) position++;
        return position == query.Length;
    }
}

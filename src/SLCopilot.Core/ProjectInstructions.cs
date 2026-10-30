namespace SLCopilot.Core;

/// <summary>项目级自定义指令（由项目团队维护，随请求发送给模型）。</summary>
public sealed record ProjectInstructions(string RelativePath, string Text)
{
    /// <summary>用户级指令（对所有解决方案生效），不是项目团队的指令。</summary>
    public bool IsUser { get; init; }

    /// <summary>用户级指令文件：%USERPROFILE%\.slcopilot\instructions.md。</summary>
    public static string UserPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".slcopilot", "instructions.md");

    /// <summary>读取用户级指令；不存在、过大、为空或无法读取时返回 null。</summary>
    public static ProjectInstructions? LoadUser(string? path = null)
    {
        path ??= UserPath;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxBytes) return null;
            var text = new TextCodec(null).Decode(File.ReadAllBytes(path)).Text.Trim();
            if (text.Length == 0) return null;
            if (text.Length > MaxChars) text = text.Substring(0, MaxChars) + "\n[Instructions truncated]";
            return new("~/.slcopilot/instructions.md", text) { IsUser = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    public const int MaxChars = 16_000;
    private const int MaxBytes = 65_536;

    /// <summary>按顺序查找的指令文件；兼容 GitHub Copilot 的文件位置。</summary>
    public static readonly string[] Candidates = [".slcopilot/instructions.md", ".github/copilot-instructions.md"];

    public string Hash => Compat.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(Text));

    /// <summary>
    /// 读取工作区根目录下第一个存在的指令文件。文件受工作区策略约束（忽略规则、链接检查）；
    /// 不存在、被策略排除、过大或无法解码时返回 null（指令是可选的，不阻断请求）。
    /// </summary>
    public static ProjectInstructions? Load(string root)
    {
        WorkspacePolicy policy;
        try { policy = new WorkspacePolicy(root); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
        foreach (var candidate in Candidates)
        {
            try
            {
                var full = policy.Resolve(candidate);
                if (!File.Exists(full) || new FileInfo(full).Length > MaxBytes) continue;
                var text = new TextCodec(null).Decode(File.ReadAllBytes(full)).Text.Trim();
                if (text.Length == 0) continue;
                if (text.Length > MaxChars) text = text.Substring(0, MaxChars) + "\n[Instructions truncated]";
                return new(candidate, text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return null;
    }

    /// <summary>
    /// 把一条用户要求记入项目指令文件（/remember）：追加到第一个已存在的指令文件末尾，都不存在时新建 .slcopilot/instructions.md。
    /// 先写临时文件再替换，原文件保留为 .bak；文件受工作区策略约束。返回写入的相对路径。失败时抛出 IOException 或 WorkspaceException，原文件不变。
    /// </summary>
    public static string Remember(string root, string note)
    {
        note = note.Trim().Replace("\r\n", "\n").Replace('\n', ' ');
        if (note.Length == 0) throw new ArgumentException("Nothing to remember.");
        if (note.Length > 1000) throw new ArgumentException("Keep a remembered note under 1,000 characters.");
        var policy = new WorkspacePolicy(root);
        var relative = Candidates.FirstOrDefault(c => File.Exists(policy.Resolve(c))) ?? Candidates[0];
        var full = policy.Resolve(relative);
        var existing = File.Exists(full) ? File.ReadAllText(full) : "";
        if (existing.Length + note.Length > MaxBytes) throw new IOException("The instructions file is too large to extend.");
        var text = existing.Length == 0 ? "# 项目指令\n\n- " + note + "\n"
            : existing + (existing.EndsWith("\n", StringComparison.Ordinal) ? "" : "\n") + "- " + note + "\n";
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".tmp";
        File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false));
        if (File.Exists(full)) File.Replace(temp, full, full + ".bak");
        else File.Move(temp, full);
        return relative;
    }

    /// <summary>附加到用户消息前的指令块，说明其来源与优先级。</summary>
    public string ToPromptBlock() => IsUser
        ? $"[Personal instructions from {RelativePath}, written by the user for all their projects. Follow them unless they conflict with safety rules, the project instructions or the user's explicit request.]\n<user_instructions>\n{Text}\n</user_instructions>"
        : $"[Project instructions from {RelativePath}, written by the project team. Follow them for this conversation unless they conflict with safety rules or the user's explicit request.]\n<project_instructions>\n{Text}\n</project_instructions>";
}

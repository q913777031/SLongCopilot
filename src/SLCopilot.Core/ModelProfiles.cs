using System.Text.Json;

namespace SLCopilot.Core;

public sealed record ModelProfile(string Id, string Name, ProviderSettings Settings);

public sealed record ModelProfileCatalog
{
    public int Version { get; init; } = 1;
    /// <summary>默认选中 Claude 配置（Sonnet 5.5，思考强度中：比 Opus 5.5 更快、token 单价更低）。</summary>
    public string SelectedProfileId { get; init; } = "claude";
    public ModelProfile[] Profiles { get; init; } = [];
    /// <summary>是否启用行内补全（默认关闭：开启后输入时会把光标附近代码发送给当前配置的服务）。</summary>
    public bool InlineCompletions { get; init; }
    /// <summary>
    /// 自动批准（默认开启）：智能体的写入、删除、移动、编译、测试、命令与 MCP 调用不再弹出审批，直接执行（写入仍校验版本并备份、可撤销）。
    /// 关闭后逐次审批。工作区 MCP 配置的信任确认与恢复检查点的确认不受影响。
    /// </summary>
    public bool AutoApprove { get; init; } = true;
    /// <summary>
    /// 智能体运行前自动保存工作区内有未保存修改的文件（默认开启，与 VS 编译前自动保存一致）。编辑器中有未保存修改的文件不能被智能体修改；
    /// 关闭后这些文件在运行中只读。
    /// </summary>
    public bool SaveBeforeAgentRun { get; init; } = true;
    /// <summary>智能体每次运行最多的回合数（5–100，默认 50）；达到上限时保留改动与对话，可发送“继续”。</summary>
    public int AgentMaxTurns { get; init; } = AgentRunner.DefaultMaxTurns;
    /// <summary>智能体单文件上限（KiB）：整文件读取、写入与撤销的最大文件；更大的文件只能分段读取。16–32768。</summary>
    public int AgentMaxFileKiB { get; init; } = WorkspaceTools.DefaultMaxFileBytes / 1024;
    /// <summary>智能体 run_command / run_tests 可执行的命令白名单（格式见 <see cref="CommandAllowlist"/>）；空数组表示不允许任何命令。</summary>
    public string[] AllowedCommands { get; init; } = CommandAllowlist.Defaults;
    /// <summary>
    /// Claude CLI 桥接（默认关闭）：开启后在 127.0.0.1:<see cref="BridgePort"/> 上以 MCP 服务提供工作区工具，文件由 VS 进程读写。
    /// 令牌保存在 Windows 凭据管理器，不在本文件中。
    /// </summary>
    public bool BridgeEnabled { get; init; }
    /// <summary>桥接监听端口（1024–65535）。</summary>
    public int BridgePort { get; init; } = McpBridgeOptions.DefaultPort;
    /// <summary>桥接是否提供写入、删除、移动、编译、测试与命令工具；关闭时只提供只读工具。</summary>
    public bool BridgeAllowWrites { get; init; } = true;

    public static ModelProfileCatalog Defaults() => new()
    {
        Profiles =
        [
            new("openai", "OpenAI", new()),
            new("claude", "Claude", new()
            {
                Provider = ProviderKind.Claude, Endpoint = "https://api.anthropic.com/v1",
                Model = "claude-sonnet-5-5", KeyEnvironmentVariable = "ANTHROPIC_API_KEY", ContextWindow = 1_000_000, MaxOutputTokens = ModelLimits.LongOutputTokens,
                Effort = "medium",
            }),
        ],
    };

    /// <summary>
    /// 把仍是旧默认值的配置升级到新默认值（只在内存中，下次保存时写回）：上下文窗口 200,000 且模型已知更大时改为已知值；
    /// 输出上限 16,000 且模型已知支持 128K 输出（当前 Claude 5 系列等）时改为 64,000。用户改过的值与其他服务的配置不动。命令白名单为 null 时使用默认白名单（文件中缺少该字段时反序列化已取默认值）。
    /// </summary>
    public ModelProfileCatalog UpgradeLegacyDefaults() => this with
    {
        Profiles = Profiles?.Select(p => p?.Settings is not { } s ? p! : p with
        {
            Settings = s with
            {
                ContextWindow = s.Provider == ProviderKind.Claude && s.ContextWindow == ModelLimits.LegacyContextWindow &&
                    ModelLimits.KnownContextWindow(s.Model) is { } known ? known : s.ContextWindow,
                MaxOutputTokens = s.MaxOutputTokens == ModelLimits.LegacyMaxOutputTokens &&
                    ModelLimits.KnownMaxOutputTokens(s.Provider, s.Model) is { } output ? output : s.MaxOutputTokens,
            },
        }).ToArray() ?? [],
        AllowedCommands = AllowedCommands ?? CommandAllowlist.Defaults,
    };

    public void Validate()
    {
        if (Version != 1 || Profiles is null || Profiles.Length is < 1 or > 50)
            throw new ArgumentException("Keep between 1 and 50 model profiles in a version 1 catalog.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in Profiles)
        {
            if (profile is null || string.IsNullOrWhiteSpace(profile.Id) || profile.Id.Length > 80 || !ids.Add(profile.Id))
                throw new ArgumentException("Model profiles must have unique nonempty IDs.");
            if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 80 || !names.Add(profile.Name.Trim()))
                throw new ArgumentException("Use a unique profile name of 1–80 characters.");
            if (profile.Settings is null) throw new ArgumentException("Provider settings are missing.");
            profile.Settings.RequestUri();
        }
        if (AgentMaxTurns is < 5 or > AgentRunner.MaxTurnsLimit)
            throw new ArgumentException($"The agent turn limit must be between 5 and {AgentRunner.MaxTurnsLimit}.");
        if (AgentMaxFileKiB is < 16 or > WorkspaceTools.MaxRangedReadBytes / 1024)
            throw new ArgumentException("The agent file size limit must be between 16 and 32768 KiB.");
        CommandAllowlist.Parse(AllowedCommands);
        if (BridgePort is < 1024 or > 65535) throw new ArgumentException("The bridge port must be between 1024 and 65535.");
        if (SelectedProfileId is null || !ids.Contains(SelectedProfileId))
            throw new ArgumentException("The selected model profile does not exist.");
    }
}

public sealed class ModelProfileStore(string path, string legacySettingsPath)
{
    public ModelProfileCatalog Load()
    {
        ModelProfileCatalog catalog;
        if (File.Exists(path))
            catalog = JsonSerializer.Deserialize<ModelProfileCatalog>(File.ReadAllText(path))
                ?? throw new ArgumentException("Model profiles file is empty.");
        else if (File.Exists(legacySettingsPath))
        {
            var legacy = new SettingsStore(legacySettingsPath).Load();
            catalog = ModelProfileCatalog.Defaults() with
            {
                SelectedProfileId = "imported",
                Profiles = new[] { new ModelProfile("imported", "Imported settings", legacy) }
                    .Concat(ModelProfileCatalog.Defaults().Profiles).ToArray(),
            };
        }
        else catalog = ModelProfileCatalog.Defaults();
        catalog = catalog.UpgradeLegacyDefaults();
        catalog.Validate();
        return catalog;
    }

    public void Save(ModelProfileCatalog catalog)
    {
        catalog.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }
}

/// <summary>Persists validated profile transitions before updating the in-memory selection.</summary>
public sealed class ModelProfileManager
{
    private readonly ModelProfileStore store;
    private ModelProfileCatalog catalog;
    public IReadOnlyList<ModelProfile> Profiles => catalog.Profiles;
    public ModelProfile Current => catalog.Profiles.Single(p => p.Id == catalog.SelectedProfileId);
    public bool InlineCompletions => catalog.InlineCompletions;

    public void SetInlineCompletions(bool enabled) => Commit(catalog with { InlineCompletions = enabled });

    public bool AutoApprove => catalog.AutoApprove;

    public void SetAutoApprove(bool enabled) => Commit(catalog with { AutoApprove = enabled });

    public bool SaveBeforeAgentRun => catalog.SaveBeforeAgentRun;

    public void SetSaveBeforeAgentRun(bool enabled) => Commit(catalog with { SaveBeforeAgentRun = enabled });

    public int AgentMaxTurns => catalog.AgentMaxTurns;

    public void SetAgentMaxTurns(int turns) => Commit(catalog with { AgentMaxTurns = turns });

    public int AgentMaxFileKiB => catalog.AgentMaxFileKiB;

    public void SetAgentMaxFileKiB(int kib) => Commit(catalog with { AgentMaxFileKiB = kib });

    public IReadOnlyList<string> AllowedCommands => catalog.AllowedCommands;

    /// <summary>保存命令白名单（规范化为每条一行）；格式错误时抛出 ArgumentException，不保存。</summary>
    public void SetAllowedCommands(IEnumerable<string> lines)
        => Commit(catalog with { AllowedCommands = CommandAllowlist.Parse(lines).Select(r => r.ToString()).ToArray() });

    public bool BridgeEnabled => catalog.BridgeEnabled;
    public int BridgePort => catalog.BridgePort;
    public bool BridgeAllowWrites => catalog.BridgeAllowWrites;

    /// <summary>保存桥接设置；端口不合法时抛出 ArgumentException，不保存。</summary>
    public void SetBridge(bool enabled, int port, bool allowWrites)
        => Commit(catalog with { BridgeEnabled = enabled, BridgePort = port, BridgeAllowWrites = allowWrites });

    public ModelProfileManager(ModelProfileStore store, ModelProfileCatalog? initial = null)
    {
        this.store = store;
        catalog = initial ?? store.Load();
        catalog.Validate();
    }

    public void SaveCurrent(string name, ProviderSettings settings)
        => Commit(WithDraft(name, settings));

    public void Select(string id, string draftName, ProviderSettings draftSettings)
    {
        if (!catalog.Profiles.Any(p => p.Id == id)) throw new ArgumentException("Unknown model profile.");
        Commit(WithDraft(draftName, draftSettings) with { SelectedProfileId = id });
    }

    public void Add(string draftName, ProviderSettings draftSettings)
    {
        var next = WithDraft(draftName, draftSettings);
        var name = "New model";
        for (var i = 2; next.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
            name = "New model " + i;
        var added = new ModelProfile(Guid.NewGuid().ToString("N"), name, draftSettings);
        Commit(next with { Profiles = next.Profiles.Concat(new[] { added }).ToArray(), SelectedProfileId = added.Id });
    }

    public void RemoveCurrent()
    {
        if (catalog.Profiles.Length == 1) throw new ArgumentException("Keep at least one model profile.");
        var remaining = catalog.Profiles.Where(p => p.Id != catalog.SelectedProfileId).ToArray();
        Commit(catalog with { Profiles = remaining, SelectedProfileId = remaining[0].Id });
    }

    private ModelProfileCatalog WithDraft(string name, ProviderSettings settings) => catalog with
    {
        Profiles = catalog.Profiles.Select(p => p.Id == catalog.SelectedProfileId ? p with { Name = name.Trim(), Settings = settings } : p).ToArray(),
    };

    private void Commit(ModelProfileCatalog next)
    {
        store.Save(next);
        catalog = next;
    }
}

using System.ComponentModel.Composition;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.Language.Proposals;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using SLCopilot.Core;
using Task = System.Threading.Tasks.Task;

namespace SLCopilot;

/// <summary>
/// 行内补全的配置与运行状态（进程级单例）。配置从 profiles.json 读取，最多每 2 秒检查一次文件时间戳；
/// 连续认证失败、限流或网络错误后暂停 60 秒，避免每次按键都失败请求。线程安全。
/// </summary>
internal static class InlineCompletionState
{
    private static readonly object Gate = new();
    private static readonly string ProfilesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SLCopilot", "profiles.json");
    private static readonly string LegacyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SLCopilot", "settings.json");
    private static DateTime checkedAt, fileTime, pausedUntil;
    private static ModelProfileCatalog? catalog;

    public static readonly HttpClient Http = new(new HttpClientHandler { DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials }) { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>已启用且未暂停时返回当前配置；否则返回 null。</summary>
    public static ModelProfile? Current()
    {
        SLCopilotPaths.EnsureLogging();
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (now < pausedUntil) return null;
            if (now - checkedAt > TimeSpan.FromSeconds(2))
            {
                checkedAt = now;
                try
                {
                    var time = File.Exists(ProfilesPath) ? File.GetLastWriteTimeUtc(ProfilesPath) : DateTime.MinValue;
                    if (catalog is null || time != fileTime)
                    {
                        fileTime = time;
                        catalog = time == DateTime.MinValue ? null : new ModelProfileStore(ProfilesPath, LegacyPath).Load();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
                {
                    catalog = null;
                    ActivityLog.LogWarning("SLCopilot", $"Inline completion settings could not be loaded: {ex.Message}");
                }
            }
            if (catalog is not { InlineCompletions: true } current) return null;
            return current.Profiles.FirstOrDefault(p => p.Id == current.SelectedProfileId);
        }
    }

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);
    private static (string ProfileId, string? Key, DateTime At) keyCache;
    private static readonly Dictionary<string, (WorkspacePolicy? Policy, DateTime At)> policyCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>读取密钥并缓存 60 秒（同一配置），避免每次补全都读凭据管理器与环境变量。</summary>
    public static string? Key(ModelProfile profile)
    {
        lock (Gate)
        {
            if (keyCache.ProfileId == profile.Id && DateTime.UtcNow - keyCache.At < CacheLifetime) return keyCache.Key;
        }
        var key = ApiKeys.Read(profile.Settings, profile.Id, out _);
        lock (Gate) keyCache = (profile.Id, key, DateTime.UtcNow);
        return key;
    }

    /// <summary>
    /// 文件所在目录对应的工作区规则（最近的 .git / 解决方案目录，找不到时为该目录本身），按目录缓存 60 秒；
    /// 规则无法建立（如目录是盘符根或用户目录）时为 null，表示不补全。
    /// </summary>
    public static WorkspacePolicy? PolicyFor(string? directory)
    {
        if (string.IsNullOrEmpty(directory)) return null;
        lock (Gate)
        {
            if (policyCache.TryGetValue(directory!, out var cached) && DateTime.UtcNow - cached.At < CacheLifetime) return cached.Policy;
        }
        WorkspacePolicy? policy = null;
        try
        {
            var root = directory;
            for (var current = directory; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if (Directory.Exists(Path.Combine(current, ".git")) || Directory.EnumerateFiles(current, "*.sln").Any() || Directory.EnumerateFiles(current, "*.slnx").Any())
                {
                    root = current;
                    break;
                }
            policy = new WorkspacePolicy(root!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        lock (Gate)
        {
            if (policyCache.Count > 200) policyCache.Clear();
            policyCache[directory!] = (policy, DateTime.UtcNow);
        }
        return policy;
    }

    public static void Pause(string reason)
    {
        lock (Gate)
        {
            if (DateTime.UtcNow < pausedUntil) return;
            pausedUntil = DateTime.UtcNow.AddSeconds(60);
        }
        ActivityLog.LogWarning("SLCopilot", $"Inline completions paused for 60 seconds: {reason}");
        RunLog.Warn("inline", "inline_paused", ("reason", reason), ("seconds", 60));
    }

    /// <summary>光标后只有空白或闭合符号时才补全（与 Copilot 一致，避免在一行中间插入）。</summary>
    public static bool IsCompletionPosition(VirtualSnapshotPoint caret, ProposalScenario scenario)
    {
        if (scenario is not (ProposalScenario.TypeChar or ProposalScenario.Return or ProposalScenario.ExplicitInvocation)) return false;
        var line = caret.Position.GetContainingLine();
        if (line.Snapshot.Length > 2_000_000) return false;
        var rest = line.Snapshot.GetText(caret.Position.Position, line.End.Position - caret.Position.Position);
        return Regex.IsMatch(rest, @"^[\s\)\]\}\;,""'>]*$");
    }
}

/// <summary>告诉编辑器在哪些位置向补全源请求建议；未启用行内补全时不参与判断，交给其他管理器（如 IntelliCode）。</summary>
[Export(typeof(ProposalManagerProviderBase))]
[Name("SLCopilotInlineProposalManager")]
[ContentType("code")]
internal sealed class InlineProposalManagerProvider : ProposalManagerProviderBase
{
    public override Task<ProposalManagerBase?> GetProposalManagerAsync(ITextView view, CancellationToken cancel)
        => Task.FromResult<ProposalManagerBase?>(new Manager());

    private sealed class Manager : ProposalManagerBase
    {
        public override bool TryGetIsProposalPosition(VirtualSnapshotPoint caret, ProposalScenario scenario, char triggeringCharacter, ref bool isProposalPosition)
        {
            isProposalPosition = false;
            if (InlineCompletionState.Current() is null || !InlineCompletionState.IsCompletionPosition(caret, scenario)) return false;
            isProposalPosition = true;
            return true;
        }

        public override Task DisposeAsync() => Task.CompletedTask;
    }
}

/// <summary>
/// 行内补全源：停顿 400 毫秒后把光标前后有限的代码发送给当前模型配置，返回灰字建议（Tab 接受）。
/// 只处理磁盘上的、工作区策略允许的源文件；IntelliSense 列表打开时不提供建议。失败时静默返回 null。
/// </summary>
[Export(typeof(ProposalSourceProviderBase))]
[Name("SLCopilotInlineProposalSource")]
[ContentType("code")]
internal sealed class InlineProposalSourceProvider : ProposalSourceProviderBase
{
    public override Task<ProposalSourceBase?> GetProposalSourceAsync(ITextView view, CancellationToken cancel)
        => Task.FromResult<ProposalSourceBase?>(new Source(view));

    private sealed class Source(ITextView view) : ProposalSourceBase
    {
        private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

        public override async Task<ProposalCollectionBase?> RequestProposalsAsync(VirtualSnapshotPoint caret, CompletionState? completionState,
            ProposalScenario scenario, char triggeringCharacter, CancellationToken cancel)
        {
            if (completionState is not null || InlineCompletionState.Current() is null || !InlineCompletionState.IsCompletionPosition(caret, scenario)) return null;
            if (!view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document) || string.IsNullOrEmpty(document.FilePath)) return null;
            var path = document.FilePath;
            await Task.Delay(Debounce, cancel).ConfigureAwait(false);
            if (InlineCompletionState.Current() is not { } profile || !IsAllowed(path)) return null;
            var key = InlineCompletionState.Key(profile);
            if (key is null) return null;

            var snapshot = caret.Position.Snapshot;
            var position = caret.Position.Position;
            var line = caret.Position.GetContainingLine();
            var indent = new string(' ', caret.VirtualSpaces);
            var prefixStart = Math.Max(0, position - InlineCompletion.MaxPrefixChars);
            var prefix = snapshot.GetText(prefixStart, position - prefixStart) + indent;
            var suffix = snapshot.GetText(position, Math.Min(InlineCompletion.MaxSuffixChars, snapshot.Length - position));
            var linePrefix = snapshot.GetText(line.Start.Position, position - line.Start.Position) + indent;
            var lineSuffix = snapshot.GetText(position, line.End.Position - position);
            string? text;
            try
            {
                text = await InlineCompletion.CompleteAsync(InlineCompletionState.Http, profile.Settings, key, Path.GetFileName(path),
                    prefix, suffix, linePrefix, lineSuffix, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return null; }
            catch (ProviderHttpException ex)
            {
                if (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or (HttpStatusCode)429)
                    InlineCompletionState.Pause($"HTTP {(int)ex.Status}");
                return null;
            }
            catch (Exception ex) when (ex is HttpRequestException or ArgumentException or System.Text.Json.JsonException)
            {
                InlineCompletionState.Pause(ex.GetType().Name);
                return null;
            }
            if (text is null || cancel.IsCancellationRequested) return null;

            var edit = new ProposedEdit(new SnapshotSpan(caret.Position, 0), indent + text.Replace("\n", NewLine(line)));
            var proposal = Proposal.TryCreateProposal("SLCopilot", [edit], caret, completionState!, ProposalFlags.ShowCommitHighlight,
                null!, Guid.NewGuid().ToString("N"), "接受", "下一条", null!);
            return proposal is null ? null : new ProposalCollection("SLCopilot", [proposal]);
        }

        public override Task DisposeAsync() => Task.CompletedTask;

        /// <summary>沿用当前行的换行符；最后一行没有换行符时按文件第一行判断，默认 CRLF。</summary>
        private static string NewLine(ITextSnapshotLine line)
        {
            var text = line.GetLineBreakText();
            if (text.Length > 0) return text;
            var first = line.Snapshot.GetLineFromLineNumber(0).GetLineBreakText();
            return first.Length > 0 ? first : "\r\n";
        }

        /// <summary>按最近的解决方案 / Git 根目录应用工作区策略（忽略规则、敏感文件、扩展名白名单）；找不到根目录时以文件所在目录为根。</summary>
        private static bool IsAllowed(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                var policy = InlineCompletionState.PolicyFor(Path.GetDirectoryName(full));
                if (policy is null) return false;
                if (!full.StartsWith(policy.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
                policy.Resolve(full.Substring(policy.Root.Length + 1));
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }
    }
}

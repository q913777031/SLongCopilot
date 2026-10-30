using System.Collections.ObjectModel;
using SLCopilot.Core;

namespace SLCopilot;

/// <summary>
/// 对话中的一条消息（气泡）。流式输出期间显示 <see cref="PlainText"/>；完成后追加 Markdown 片段并隐藏纯文本。
/// 只能在 UI 线程修改。
/// </summary>
public sealed class ChatEntry(string role, string header) : ObservableObject
{
    private string plainText = "";
    private bool showPlain = true, isPending;

    /// <summary>user 或 assistant，决定气泡样式。</summary>
    public string Role { get; } = role;
    public bool IsUser => Role == "user";
    public string Header { get; } = header;
    public string PlainText { get => plainText; private set => SetProperty(ref plainText, value); }
    public bool ShowPlain { get => showPlain; private set => SetProperty(ref showPlain, value); }
    public ObservableCollection<object> Parts { get; } = [];
    /// <summary>为 true 时在气泡底部显示“思考中…”；收到首段流式文本或请求结束时置为 false。</summary>
    public bool IsPending { get => isPending; set => SetProperty(ref isPending, value); }

    /// <summary>复制这条消息的文字：回答为各段 Markdown 原文（不含工具行与差异），提问为输入的文字。</summary>
    public System.Windows.Input.ICommand CopyCommand => copyCommand ??= new RelayCommand(() =>
    {
        var texts = Parts.OfType<MarkdownPart>().Where(p => !p.IsPlan).Select(p => p.IsRawCode ? "```\n" + p.Source + "\n```" : p.Source).ToList();
        if (PlainText.Length > 0) texts.Insert(0, PlainText);
        var text = string.Join("\n\n", texts).Trim();
        if (text.Length == 0) return;
        try { System.Windows.Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException ex) { Microsoft.VisualStudio.Shell.ActivityLog.LogWarning("SLCopilot", $"Copying the message failed: {ex.Message}"); }
    });
    private System.Windows.Input.ICommand? copyCommand;

    /// <summary>
    /// 提问气泡上的“回退到此前”：这次提问产生了有改动的运行（检查点）时由视图模型设置，恢复该检查点（文件与对话都回到提问之前）；检查点移除后清空。
    /// </summary>
    public System.Windows.Input.ICommand? RewindCommand
    {
        get => rewindCommand;
        set { if (SetProperty(ref rewindCommand, value)) RaisePropertyChanged(nameof(CanRewind)); }
    }
    public bool CanRewind => rewindCommand is not null;
    private System.Windows.Input.ICommand? rewindCommand;

    public void SetStreamingText(string text)
    {
        PlainText = text;
        ShowPlain = text.Length > 0;
        if (text.Length > 0) IsPending = false;
    }

    public void Complete(string markdown)
    {
        Parts.Add(new MarkdownPart(markdown, Markdown.Parse(markdown)));
        PlainText = "";
        ShowPlain = false;
    }

    public void AddCode(string code)
    {
        Parts.Add(new MarkdownPart(code, [new MdBlock(MdBlockKind.Code, code, [])]) { IsRawCode = true });
        PlainText = "";
        ShowPlain = false;
    }

    /// <summary>显示或更新智能体的任务计划：已有计划时原位替换，保持在气泡中的位置不变。</summary>
    public void SetPlan(string markdown)
    {
        var part = new MarkdownPart(markdown, Markdown.Parse(markdown)) { IsPlan = true };
        for (var i = 0; i < Parts.Count; i++)
            if (Parts[i] is MarkdownPart { IsPlan: true }) { Parts[i] = part; return; }
        Parts.Add(part);
    }

    /// <summary>
    /// 显示或更新本次运行改动的文件卡片。已有卡片时原位更新；<paramref name="moveToEnd"/> 为 true 时（运行结束）移到气泡末尾。
    /// 文件列表为空时移除卡片。卡片只在本次 VS 会话中显示，不随对话保存。
    /// </summary>
    /// <param name="workspace">产生这些改动的运行；用于逐个恢复与撤销整次运行。为 null 时不显示恢复按钮。</param>
    public void SetChangedFiles(IReadOnlyList<ChangedFile> files, bool moveToEnd, WorkspaceTools? workspace = null)
    {
        var index = -1;
        for (var i = 0; i < Parts.Count; i++)
            if (Parts[i] is ChangedFilesPart) { index = i; break; }
        if (files.Count == 0)
        {
            if (index >= 0) Parts.RemoveAt(index);
            return;
        }
        var card = index >= 0 ? (ChangedFilesPart)Parts[index] : new ChangedFilesPart();
        if (workspace is not null) card.Workspace = workspace;
        card.Update(files);
        if (index < 0) Parts.Add(card);
        else if (moveToEnd && index != Parts.Count - 1) Parts.Move(index, Parts.Count - 1);
    }

    /// <summary>在用户气泡中显示随提问发送的图片缩略图。保存对话时只保留文字说明。</summary>
    public void AddImages(IEnumerable<PendingImage> images)
    {
        var list = images.ToList();
        if (list.Count > 0) Parts.Add(new ImagesPart(list));
    }

    /// <summary>在工具调用行之后显示一次写入的内联差异。<paramref name="display"/> 为“相对路径 + 换行 + 差异”；差异为空时不显示。</summary>
    public void AddDiff(string display)
    {
        var newline = display.IndexOf('\n');
        if (newline <= 0 || newline == display.Length - 1) return;
        Parts.Add(new DiffPart(display.Substring(0, newline), display.Substring(newline + 1)));
    }

    /// <summary>追加一行工具调用或运行状态（灰色小字；错误为红色）。保留已显示的流式文本。返回新加的行，供之后补充结果。</summary>
    public ActivityPart AddActivity(string text, bool isError = false)
    {
        var part = new ActivityPart(text, isError);
        Parts.Add(part);
        return part;
    }

    /// <summary>在已显示的某一行末尾追加文字（如工具结果摘要），并可附上点击展开的详情。</summary>
    public static void AppendToActivity(ActivityPart line, string suffix, string? detail = null)
    {
        line.Text += suffix;
        if (!string.IsNullOrEmpty(detail)) line.Detail = detail;
    }

    /// <summary>
    /// 把连续 <paramref name="minRun"/> 行以上的非错误活动行（工具调用与状态）折叠为一行，可点击展开。运行结束或加载历史对话时调用；
    /// 差异、回答文字与错误行不折叠，并会打断连续段。
    /// </summary>
    public void CollapseActivities(int minRun = 6)
    {
        var result = new List<object>();
        var run = new List<ActivityPart>();
        void Flush()
        {
            if (run.Count >= minRun) result.Add(new ActivityGroupPart(run.ToList()));
            else result.AddRange(run);
            run.Clear();
        }
        foreach (var part in Parts)
        {
            if (part is ActivityPart { IsError: false } line) { run.Add(line); continue; }
            Flush();
            result.Add(part);
        }
        Flush();
        if (result.Count == Parts.Count) return;
        Parts.Clear();
        foreach (var part in result) Parts.Add(part);
    }

    /// <summary>导出为可持久化的记录；流式期间的未完成文本作为用户文本或回答纯文本保留。折叠的活动行按原样逐行导出。</summary>
    public ConversationEntry Export() => new(Role, Header, PlainText, Parts.Where(part => part is not ChangedFilesPart)
        .SelectMany(part => part is ActivityGroupPart group ? group.Items.Cast<object>() : [part]).Select(part => part switch
    {
        MarkdownPart { IsRawCode: true } code => new ConversationPart("code", code.Source),
        MarkdownPart markdown => new ConversationPart("markdown", markdown.Source),
        ActivityPart activity => new ConversationPart(activity.IsError ? "error" : "activity", activity.Text),
        DiffPart diff => new ConversationPart("markdown", $"`{diff.Path}`\n\n```diff\n{diff.Raw}\n```"),
        ImagesPart images => new ConversationPart("activity", $"🖼 附带了 {images.Images.Count} 张图片（{string.Join("、", images.Images.Select(i => i.Attachment.Name))}，图片未随对话保存）"),
        _ => new ConversationPart("markdown", ""),
    }).ToArray());

    private static readonly System.Text.RegularExpressions.Regex SavedDiff =
        new(@"\A`(?<path>[^`\n]+)`\n\n```diff\n(?<diff>[\s\S]*)\n```\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>恢复差异块；路径不是合法文件路径（普通回答恰好是同样的格式）时返回 null，按 Markdown 显示。</summary>
    private static DiffPart? TryDiffPart(string path, string diff)
    {
        try { return new DiffPart(path, diff); }
        catch (ArgumentException) { return null; }
    }

    public static ChatEntry FromRecord(ConversationEntry record)
    {
        var entry = new ChatEntry(record.Role, record.Header);
        entry.SetStreamingText(record.Text ?? "");
        foreach (var part in record.Parts ?? [])
        {
            switch (part.Kind)
            {
                case "markdown":
                    // 保存时写成 Markdown 的内联差异（见 Export）：恢复为带颜色的差异块。
                    if (SavedDiff.Match(part.Text) is { Success: true } diff && TryDiffPart(diff.Groups["path"].Value, diff.Groups["diff"].Value) is { } restored) entry.Parts.Add(restored);
                    else entry.Parts.Add(new MarkdownPart(part.Text, Markdown.Parse(part.Text)));
                    break;
                case "code": entry.Parts.Add(new MarkdownPart(part.Text, [new MdBlock(MdBlockKind.Code, part.Text, [])]) { IsRawCode = true }); break;
                case "activity": entry.Parts.Add(new ActivityPart(part.Text, false)); break;
                case "error": entry.Parts.Add(new ActivityPart(part.Text, true)); break;
            }
        }
        if (entry.Parts.Count > 0 && entry.PlainText.Length == 0) entry.ShowPlain = false;
        entry.CollapseActivities();
        return entry;
    }
}

public sealed record MarkdownPart(string Source, IReadOnlyList<MdBlock> Blocks)
{
    public bool IsRawCode { get; init; }
    /// <summary>智能体的任务计划；更新时原位替换。</summary>
    public bool IsPlan { get; init; }
}

/// <summary>
/// 气泡中的一行活动（工具调用、状态，灰色小字；错误为红色）。有 <see cref="Detail"/> 时可点击展开，查看工具返回给模型的内容。只能在 UI 线程修改。
/// </summary>
public sealed class ActivityPart : ObservableObject
{
    private string text;
    private string? detail;
    private bool isExpanded;

    public ActivityPart(string text, bool isError, string? detail = null)
    {
        this.text = text;
        IsError = isError;
        this.detail = detail;
        ToggleCommand = new RelayCommand(() => { if (HasDetail) IsExpanded = !IsExpanded; });
    }

    public string Text { get => text; set => SetProperty(ref text, value); }
    public bool IsError { get; }
    public string? Detail
    {
        get => detail;
        set { if (SetProperty(ref detail, value)) RaisePropertyChanged(nameof(HasDetail)); }
    }
    public bool HasDetail => !string.IsNullOrEmpty(detail);
    public bool IsExpanded { get => isExpanded; set => SetProperty(ref isExpanded, value); }
    public System.Windows.Input.ICommand ToggleCommand { get; }
}

/// <summary>折叠后的连续活动行：显示为一行“▸ N 条工具记录”，点击展开。只能在 UI 线程修改。</summary>
public sealed class ActivityGroupPart : ObservableObject
{
    private bool isExpanded;

    public ActivityGroupPart(IReadOnlyList<ActivityPart> items)
    {
        Items = items;
        var tools = items.Count(i => i.Text.StartsWith("▸", StringComparison.Ordinal));
        Title = tools > 0 ? $"{tools} 次工具调用" + (items.Count > tools ? $"，{items.Count - tools} 条状态" : "") : $"{items.Count} 条记录";
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public IReadOnlyList<ActivityPart> Items { get; }
    public string Title { get; }
    public bool IsExpanded { get => isExpanded; set { if (SetProperty(ref isExpanded, value)) RaisePropertyChanged(nameof(Glyph)); } }
    public string Glyph => isExpanded ? "▾" : "▸";
    public System.Windows.Input.ICommand ToggleCommand { get; }
}

/// <summary>
/// 差异中的一行：行号（新增与上下文为新行号，删除为旧行号）、文本与种类（added / removed / context / gap）。
/// 成对的删除 / 新增行把文本分为相同的前缀、改动部分与相同的后缀，改动部分加深显示。
/// </summary>
public sealed record DiffLine(string Number, string Text, string Kind)
{
    /// <summary>着色用的语言（来自文件扩展名，如 cs、xaml）；为空时不着色。</summary>
    public string Language { get; init; } = "";
    public string Before { get; init; } = Text;
    public string Changed { get; init; } = "";
    public string After { get; init; } = "";
}

/// <summary>
/// 一次写入的内联差异（仿 Claude Code 的 Update 块）：路径、增删行数与差异行。超过 <see cref="CollapsedCount"/> 行时先折叠，可展开。
/// 只能在 UI 线程修改。
/// </summary>
public sealed class DiffPart : ObservableObject
{
    public const int CollapsedCount = 14;
    private readonly IReadOnlyList<DiffLine> all;
    private bool expanded;

    public DiffPart(string path, string diff)
    {
        Path = path;
        Raw = diff;
        var lines = new List<DiffLine>();
        int oldLine = 0, newLine = 0, added = 0, removed = 0;
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"^@@ -(\d+) \+(\d+) @@");
                if (match.Success)
                {
                    oldLine = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    newLine = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                }
                if (lines.Count > 0) lines.Add(new DiffLine("", "⋯", "gap"));
                continue;
            }
            if (line.Length == 0) continue;
            switch (line[0])
            {
                case '+': lines.Add(new DiffLine(newLine++.ToString(System.Globalization.CultureInfo.InvariantCulture), line.Substring(1), "added")); added++; break;
                case '-': lines.Add(new DiffLine(oldLine++.ToString(System.Globalization.CultureInfo.InvariantCulture), line.Substring(1), "removed")); removed++; break;
                case ' ': lines.Add(new DiffLine(newLine.ToString(System.Globalization.CultureInfo.InvariantCulture), line.Substring(1), "context")); oldLine++; newLine++; break;
                default: lines.Add(new DiffLine("", line, "gap")); break;
            }
        }
        HighlightChangedWords(lines);
        var language = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        for (var i = 0; i < lines.Count; i++) if (lines[i].Kind != "gap") lines[i] = lines[i] with { Language = language };
        all = lines;
        var firstChanged = lines.FirstOrDefault(l => l.Kind is "added" or "removed");
        Reference = firstChanged is { Number.Length: > 0 } ? $"{path}:{firstChanged.Number}" : path;
        Added = "+" + added.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Removed = "−" + removed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ExpandCommand = new RelayCommand(() => { expanded = true; RaisePropertyChanged(nameof(Lines)); RaisePropertyChanged(nameof(CanExpand)); });
    }

    /// <summary>连续 n 行删除紧接 n 行新增时逐行配对，标出每对之间不同的部分（相同的前缀与后缀之外）；改动超过整行时不标。</summary>
    private static void HighlightChangedWords(List<DiffLine> lines)
    {
        for (var i = 0; i < lines.Count;)
        {
            var removedEnd = i;
            while (removedEnd < lines.Count && lines[removedEnd].Kind == "removed") removedEnd++;
            var addedEnd = removedEnd;
            while (addedEnd < lines.Count && lines[addedEnd].Kind == "added") addedEnd++;
            var count = removedEnd - i;
            if (count > 0 && addedEnd - removedEnd == count)
                for (var k = 0; k < count; k++)
                {
                    var (old, @new) = (lines[i + k].Text, lines[removedEnd + k].Text);
                    var prefix = 0;
                    while (prefix < old.Length && prefix < @new.Length && old[prefix] == @new[prefix]) prefix++;
                    var suffix = 0;
                    while (suffix < old.Length - prefix && suffix < @new.Length - prefix && old[old.Length - 1 - suffix] == @new[@new.Length - 1 - suffix]) suffix++;
                    if (prefix + suffix == 0) continue;
                    lines[i + k] = Split(lines[i + k], prefix, suffix);
                    lines[removedEnd + k] = Split(lines[removedEnd + k], prefix, suffix);
                }
            i = Math.Max(addedEnd, i + 1);
        }

        static DiffLine Split(DiffLine line, int prefix, int suffix) => line with
        {
            Before = line.Text.Substring(0, prefix),
            Changed = line.Text.Substring(prefix, line.Text.Length - prefix - suffix),
            After = line.Text.Substring(line.Text.Length - suffix),
        };
    }

    public string Path { get; }
    /// <summary>点击标题时打开的引用：“路径:第一处改动的行号”。</summary>
    public string Reference { get; }
    /// <summary>原始差异文本（保存对话时使用）。</summary>
    public string Raw { get; }
    public string Added { get; }
    public string Removed { get; }
    public IReadOnlyList<DiffLine> Lines => expanded ? all : all.Take(CollapsedCount).ToList();
    public bool CanExpand => !expanded && all.Count > CollapsedCount;
    public string ExpandText => $"显示全部 {all.Count} 行";
    public System.Windows.Input.ICommand ExpandCommand { get; }
}

/// <summary>
/// 智能体回答末尾的“已修改文件”卡片（仿 Copilot）：每行一个文件及其新增 / 删除行数，点击在差异窗口中查看改动。
/// 默认显示前 <see cref="CollapsedCount"/> 个，可展开全部。只能在 UI 线程修改。
/// </summary>
public sealed class ChangedFilesPart : ObservableObject
{
    public const int CollapsedCount = 5;
    private IReadOnlyList<ChangedFileItem> all = [];
    private bool showAll;
    private string title = "", totalAdded = "", totalRemoved = "";

    public ObservableCollection<ChangedFileItem> Visible { get; } = [];
    /// <summary>产生这些改动的运行；运行结束后设置，之后可逐个恢复或撤销整次运行。</summary>
    public WorkspaceTools? Workspace
    {
        get => workspace;
        set { if (SetProperty(ref workspace, value)) RaisePropertyChanged(nameof(CanRestore)); }
    }
    public bool CanRestore => Workspace is not null && all.Count > 0;
    private WorkspaceTools? workspace;
    public string Title { get => title; private set => SetProperty(ref title, value); }
    public string TotalAdded { get => totalAdded; private set => SetProperty(ref totalAdded, value); }
    public string TotalRemoved { get => totalRemoved; private set => SetProperty(ref totalRemoved, value); }
    /// <summary>文件数超过 <see cref="CollapsedCount"/> 且未展开时显示“显示全部”。</summary>
    public bool CanShowAll => !showAll && all.Count > CollapsedCount;
    public string ShowAllText => $"显示全部 {all.Count} 个文件";
    public System.Windows.Input.ICommand ShowAllCommand { get; }

    public ChangedFilesPart() => ShowAllCommand = new RelayCommand(() => { showAll = true; Refresh(); });

    public void Update(IReadOnlyList<ChangedFile> files)
    {
        all = files.Select(f => new ChangedFileItem(f, this)).ToList();
        Title = all.Count == 0 ? "改动已全部恢复" : $"已修改 {all.Count} 个文件";
        RaisePropertyChanged(nameof(CanRestore));
        TotalAdded = "+" + files.Sum(f => f.Added).ToString("N0");
        TotalRemoved = "−" + files.Sum(f => f.Removed).ToString("N0");
        Refresh();
    }

    private void Refresh()
    {
        Visible.Clear();
        foreach (var item in showAll ? all : all.Take(CollapsedCount)) Visible.Add(item);
        RaisePropertyChanged(nameof(CanShowAll));
        RaisePropertyChanged(nameof(ShowAllText));
    }
}

/// <summary>卡片中的一行：文件名、所在目录、新增 / 删除行数与状态说明。</summary>
public sealed class ChangedFileItem(ChangedFile file, ChangedFilesPart owner)
{
    public ChangedFile File { get; } = file;
    /// <summary>所在的卡片：恢复后据此刷新。</summary>
    public ChangedFilesPart Owner { get; } = owner;
    public string Name { get; } = file.Path.Substring(file.Path.LastIndexOf('/') + 1);
    public string Folder { get; } = file.Path.LastIndexOf('/') is var slash and > 0 ? file.Path.Substring(0, slash) : "";
    public string Added { get; } = "+" + file.Added.ToString("N0");
    public string Removed { get; } = "−" + file.Removed.ToString("N0");
    public string Note { get; } = !file.Complete ? "写入未完成" : file.Kind switch
    {
        "created" => "新建",
        "deleted" => "已删除",
        "created_and_deleted" => "新建后删除",
        _ => "",
    };
    public string ToolTip { get; } = file.Path + "\n点击在差异窗口中查看改动";
}

/// <summary>
/// 待发送（或已随提问发送）的一张图片：编码后的数据、缩略图与说明文字。<see cref="RemoveCommand"/> 由视图模型设置，用于从输入区移除。
/// </summary>
public sealed class PendingImage(ImageAttachment attachment, System.Windows.Media.ImageSource thumbnail)
{
    public ImageAttachment Attachment { get; } = attachment;
    public System.Windows.Media.ImageSource Thumbnail { get; } = thumbnail;
    public string Label { get; } = $"{attachment.Name} · {attachment.Width}×{attachment.Height} · {attachment.Data.Length / 1024.0:0} KB";
    public System.Windows.Input.ICommand? RemoveCommand { get; init; }
}

/// <summary>用户气泡中的图片缩略图行。</summary>
public sealed record ImagesPart(IReadOnlyList<PendingImage> Images);

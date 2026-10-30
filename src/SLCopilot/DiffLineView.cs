using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SLCopilot.Core;

namespace SLCopilot;

/// <summary>
/// 内联差异中的一行代码：按语言着色（与回答中的代码块配色相同），成对删改行中改动的部分加深底色。只在 UI 线程使用。
/// </summary>
public sealed class DiffLineView : TextBlock
{
    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(
        nameof(Line), typeof(DiffLine), typeof(DiffLineView), new PropertyMetadata(null, (d, _) => ((DiffLineView)d).Render()));

    /// <summary>改动部分的底色。</summary>
    public static readonly DependencyProperty ChangedBrushProperty = DependencyProperty.Register(
        nameof(ChangedBrush), typeof(Brush), typeof(DiffLineView), new PropertyMetadata(null, (d, _) => ((DiffLineView)d).Render()));

    public DiffLine? Line
    {
        get => (DiffLine?)GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    public Brush? ChangedBrush
    {
        get => (Brush?)GetValue(ChangedBrushProperty);
        set => SetValue(ChangedBrushProperty, value);
    }

    private void Render()
    {
        Inlines.Clear();
        if (Line is not { } line) return;
        var text = line.Text;
        var palette = MarkdownView.CurrentPalette;
        var kinds = new CodeTokenKind?[text.Length];
        foreach (var token in CodeHighlighter.Tokenize(text, line.Language))
            for (var i = token.Start; i < token.Start + token.Length && i < text.Length; i++) kinds[i] = token.Kind;
        var changedStart = line.Before.Length;
        var changedEnd = changedStart + line.Changed.Length;
        // 按“着色种类 + 是否在改动部分”切分为若干段，每段一个 Run。
        for (var start = 0; start < text.Length;)
        {
            var kind = kinds[start];
            var changed = start >= changedStart && start < changedEnd;
            var end = start + 1;
            while (end < text.Length && kinds[end] == kind && (end >= changedStart && end < changedEnd) == changed) end++;
            var run = new Run(text.Substring(start, end - start));
            if (kind is { } k && palette.TryGetValue(k, out var brush)) run.Foreground = brush;
            if (changed && ChangedBrush is not null) run.Background = ChangedBrush;
            Inlines.Add(run);
            start = end;
        }
    }
}

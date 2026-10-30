using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using SLCopilot.Core;

namespace SLCopilot;

/// <summary>
/// 把 <see cref="MdBlock"/> 列表渲染为 WPF 元素：段落与列表为 TextBlock 行内格式，代码块与表格为带“复制”按钮的等宽卡片。
/// 链接只打开 http/https 地址。只能在 UI 线程使用。
/// </summary>
public sealed class MarkdownView : ContentControl
{
    public static readonly DependencyProperty BlocksProperty = DependencyProperty.Register(
        nameof(Blocks), typeof(IReadOnlyList<MdBlock>), typeof(MarkdownView), new PropertyMetadata(null, (d, _) => ((MarkdownView)d).Render()));

    /// <summary>代码块“插入”按钮执行的命令，参数为代码文本；为 null 时不显示该按钮。</summary>
    public static readonly DependencyProperty InsertCodeCommandProperty = DependencyProperty.Register(
        nameof(InsertCodeCommand), typeof(System.Windows.Input.ICommand), typeof(MarkdownView), new PropertyMetadata(null, (d, _) => ((MarkdownView)d).Render()));

    /// <summary>点击回答中的文件或符号引用时执行的命令，参数为引用文本；为 null 时引用按普通文字显示。</summary>
    public static readonly DependencyProperty OpenReferenceCommandProperty = DependencyProperty.Register(
        nameof(OpenReferenceCommand), typeof(System.Windows.Input.ICommand), typeof(MarkdownView), new PropertyMetadata(null, (d, _) => ((MarkdownView)d).Render()));

    private static readonly Brush CodeBackground = Freeze(new SolidColorBrush(Color.FromArgb(0x29, 0x80, 0x80, 0x80)));
    private static readonly Brush Hairline = Freeze(new SolidColorBrush(Color.FromArgb(0x59, 0x80, 0x80, 0x80)));
    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas");

    public IReadOnlyList<MdBlock>? Blocks
    {
        get => (IReadOnlyList<MdBlock>?)GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    public System.Windows.Input.ICommand? InsertCodeCommand
    {
        get => (System.Windows.Input.ICommand?)GetValue(InsertCodeCommandProperty);
        set => SetValue(InsertCodeCommandProperty, value);
    }

    public System.Windows.Input.ICommand? OpenReferenceCommand
    {
        get => (System.Windows.Input.ICommand?)GetValue(OpenReferenceCommandProperty);
        set => SetValue(OpenReferenceCommandProperty, value);
    }

    /// <summary>
    /// 整条回答渲染为一个只读的 RichTextBox：正文、标题、列表与引用可以选中（含跨段落）并 Ctrl+C 复制；
    /// 代码块与表格作为嵌入的卡片（各自带“复制”，代码也可选中）。链接与代码引用单击即可打开。
    /// </summary>
    private void Render()
    {
        var document = new FlowDocument { PagePadding = new Thickness(0), TextAlignment = TextAlignment.Left };
        foreach (var block in Blocks ?? [])
            document.Blocks.Add(Create(block, InsertCodeCommand, OpenReferenceCommand));
        var view = new RichTextBox
        {
            Document = document, IsReadOnly = true, IsDocumentEnabled = true, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Background = Brushes.Transparent, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsUndoEnabled = false,
        };
        view.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        // FlowDocument 默认使用 Georgia 等字体：跟随控件（工具窗口）的字体与前景色。
        BindingOperations.SetBinding(document, FlowDocument.FontFamilyProperty, new Binding(nameof(FontFamily)) { Source = this });
        BindingOperations.SetBinding(document, FlowDocument.FontSizeProperty, new Binding(nameof(FontSize)) { Source = this });
        BindingOperations.SetBinding(document, FlowDocument.ForegroundProperty, new Binding(nameof(Foreground)) { Source = view });
        WheelForwarding.Attach(view);
        Content = view;
    }

    private static Block Create(MdBlock block, System.Windows.Input.ICommand? insert, System.Windows.Input.ICommand? open)
    {
        switch (block.Kind)
        {
            case MdBlockKind.Table when block.Rows is { Count: > 0 } rows:
                return new BlockUIContainer(TableCard(block.Text, rows, open));
            case MdBlockKind.Code:
            case MdBlockKind.Table:
                return new BlockUIContainer(CodeCard(block.Text, block.Kind == MdBlockKind.Table ? "表格" : block.Language, block.Kind == MdBlockKind.Code ? insert : null));
            case MdBlockKind.Rule:
                return new BlockUIContainer(new Border { Height = 1, Background = Hairline, Margin = new Thickness(0, 8, 0, 8) });
        }
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8), LineHeight = 20 };
        switch (block.Kind)
        {
            case MdBlockKind.Heading1: paragraph.FontSize = 18; paragraph.FontWeight = FontWeights.SemiBold; paragraph.Margin = new Thickness(0, 8, 0, 6); break;
            case MdBlockKind.Heading2: paragraph.FontSize = 16; paragraph.FontWeight = FontWeights.SemiBold; paragraph.Margin = new Thickness(0, 6, 0, 6); break;
            case MdBlockKind.Heading3: paragraph.FontSize = 14; paragraph.FontWeight = FontWeights.SemiBold; break;
            case MdBlockKind.Quote:
                paragraph.BorderBrush = Hairline;
                paragraph.BorderThickness = new Thickness(3, 0, 0, 0);
                paragraph.Padding = new Thickness(10, 0, 0, 0);
                break;
            case MdBlockKind.Bullet:
            case MdBlockKind.Numbered:
                // 悬挂缩进：标记在左，换行后的文字与首行文字对齐。
                var indent = block.Kind == MdBlockKind.Numbered ? 26 : 18;
                paragraph.Margin = new Thickness(block.Level * 18 + indent, 0, 0, 4);
                paragraph.TextIndent = -indent;
                paragraph.Inlines.Add(new Run(block.Marker + " ") { Foreground = Hairline });
                break;
        }
        AddInlines(paragraph.Inlines, block.Inlines, open);
        return paragraph;
    }

    private static TextBlock Paragraph(IReadOnlyList<MdInline> inlines, System.Windows.Input.ICommand? open)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), LineHeight = 20 };
        AddInlines(text.Inlines, inlines, open);
        return text;
    }

    private static void AddInlines(InlineCollection target, IReadOnlyList<MdInline> inlines, System.Windows.Input.ICommand? open)
    {
        foreach (var inline in inlines)
        {
            switch (inline.Kind)
            {
                case MdInlineKind.Break: target.Add(new LineBreak()); break;
                case MdInlineKind.Bold: target.Add(new Bold(new Run(inline.Text))); break;
                case MdInlineKind.Italic: target.Add(new Italic(new Run(inline.Text))); break;
                case MdInlineKind.Code:
                    var code = new Run(inline.Text) { FontFamily = CodeFont, Background = CodeBackground };
                    target.Add(open is not null && CodeReference.IsReference(inline.Text) ? Reference(code, inline.Text, open) : code);
                    break;
                case MdInlineKind.Link: target.Add(Link(inline)); break;
                default:
                    if (open is null) { target.Add(new Run(inline.Text)); break; }
                    // 正文中带行号的文件引用（如 src/Foo.cs:42）也可点击。
                    var position = 0;
                    foreach (System.Text.RegularExpressions.Match match in CodeReference.InlineFilePattern.Matches(inline.Text))
                    {
                        if (match.Index > position) target.Add(new Run(inline.Text.Substring(position, match.Index - position)));
                        target.Add(Reference(new Run(match.Value), match.Value, open));
                        position = match.Index + match.Length;
                    }
                    if (position < inline.Text.Length) target.Add(new Run(inline.Text.Substring(position)));
                    break;
            }
        }
    }

    /// <summary>可点击的文件或符号引用：点击时以引用文本执行 <paramref name="open"/>（打开文件并定位到行，或转到符号定义）。</summary>
    private static Inline Reference(Run run, string reference, System.Windows.Input.ICommand open)
    {
        var link = new Hyperlink(run) { Command = open, CommandParameter = reference, ToolTip = "在编辑器中打开 " + reference };
        link.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ControlLinkTextBrushKey);
        return link;
    }

    private static Inline Link(MdInline inline)
    {
        if (!Uri.TryCreate(inline.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new Run(inline.Text);
        var link = new Hyperlink(new Run(inline.Text)) { NavigateUri = uri, ToolTip = uri.AbsoluteUri };
        link.RequestNavigate += (_, e) =>
        {
            e.Handled = true;
            try { VsShellUtilities.OpenSystemBrowser(e.Uri.AbsoluteUri); }
            catch (Exception ex) { ActivityLog.LogWarning("SLCopilot", $"Opening link failed: {ex.Message}"); }
        };
        return link;
    }

    /// <summary>
    /// 表格：表头加粗，行间细线，单元格按行内 Markdown 渲染（代码与引用可点击），单元格最宽 360 像素、自动换行，整表过宽时水平滚动。
    /// “复制”复制对齐后的纯文本。
    /// </summary>
    private static UIElement TableCard(string text, IReadOnlyList<IReadOnlyList<IReadOnlyList<MdInline>>> rows, System.Windows.Input.ICommand? open)
    {
        var columns = rows.Max(r => r.Count);
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < columns; c++)
            {
                var cell = Paragraph(c < rows[r].Count ? rows[r][c] : [], open);
                cell.Margin = new Thickness(8, 4, 8, 4);
                cell.MaxWidth = 360;
                if (r == 0) cell.FontWeight = FontWeights.SemiBold;
                var border = new Border
                {
                    BorderBrush = Hairline, BorderThickness = new Thickness(0, 0, c < columns - 1 ? 1 : 0, r < rows.Count - 1 ? 1 : 0),
                    Background = r == 0 ? CodeBackground : Brushes.Transparent, Child = cell,
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }
        var copy = new Button { Content = "复制", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0), MinWidth = 0, FontSize = 11 };
        copy.SetResourceReference(StyleProperty, VsResourceKeys.ButtonStyleKey);
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(text); copy.Content = "已复制"; }
            catch (System.Runtime.InteropServices.ExternalException) { copy.Content = "复制失败"; }
        };
        DockPanel.SetDock(copy, Dock.Right);
        var header = new DockPanel { Margin = new Thickness(8, 3, 4, 3) };
        header.Children.Add(copy);
        header.Children.Add(new TextBlock { Text = "表格", FontSize = 11, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center });
        var stack = new StackPanel();
        stack.Children.Add(new Border { BorderBrush = Hairline, BorderThickness = new Thickness(0, 0, 0, 1), Child = header });
        var scroller = new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        ForwardMouseWheel(scroller);
        stack.Children.Add(scroller);
        return new Border
        {
            BorderBrush = Hairline, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 2, 0, 10), Child = stack,
        };
    }

    private static UIElement CodeCard(string code, string language, System.Windows.Input.ICommand? insert)
    {
        var copy = new Button { Content = "复制", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0), MinWidth = 0, FontSize = 11 };
        copy.SetResourceReference(StyleProperty, VsResourceKeys.ButtonStyleKey);
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(code); copy.Content = "已复制"; }
            catch (System.Runtime.InteropServices.ExternalException) { copy.Content = "复制失败"; }
        };
        DockPanel.SetDock(copy, Dock.Right);
        var header = new DockPanel { Margin = new Thickness(8, 3, 4, 3) };
        header.Children.Add(copy);
        if (insert is not null)
        {
            var insertButton = new Button
            {
                Content = "插入", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 4, 0), MinWidth = 0, FontSize = 11,
                Command = insert, CommandParameter = code, ToolTip = "插入到最近活动编辑器的光标处（有选区时替换选区）",
            };
            insertButton.SetResourceReference(StyleProperty, VsResourceKeys.ButtonStyleKey);
            DockPanel.SetDock(insertButton, Dock.Right);
            header.Children.Add(insertButton);
        }
        header.Children.Add(new TextBlock { Text = language.Length == 0 ? "代码" : language, FontSize = 11, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center });

        var body = CodeBody(code, language);

        var stack = new StackPanel();
        stack.Children.Add(new Border { BorderBrush = Hairline, BorderThickness = new Thickness(0, 0, 0, 1), Child = header });
        stack.Children.Add(body);
        return new Border
        {
            Background = CodeBackground, BorderBrush = Hairline, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 2, 0, 10), Child = stack,
        };
    }

    /// <summary>
    /// 只读、可选择的代码正文：按语言着色（配色随 VS 深浅主题选择），不自动换行，过宽时水平滚动。
    /// 未知语言按普通文本显示。
    /// </summary>
    private static Control CodeBody(string code, string language)
    {
        var palette = IsDarkTheme() ? DarkPalette : LightPalette;
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var position = 0;
        foreach (var token in CodeHighlighter.Tokenize(code, language))
        {
            if (token.Start > position) paragraph.Inlines.Add(new Run(code.Substring(position, token.Start - position)));
            paragraph.Inlines.Add(new Run(code.Substring(token.Start, token.Length)) { Foreground = palette[token.Kind] });
            position = token.Start + token.Length;
        }
        if (position < code.Length) paragraph.Inlines.Add(new Run(code.Substring(position)));

        // FlowDocument 默认按控件宽度换行：把页宽设为最长一行的宽度，配合水平滚动条实现不换行。
        var longest = code.Replace("\t", "    ").Split('\n').Select(l => l.TrimEnd('\r')).OrderByDescending(l => l.Length).FirstOrDefault() ?? "";
        var width = new FormattedText(longest + "  ", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(CodeFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 12, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;
        var document = new FlowDocument(paragraph)
        {
            FontFamily = CodeFont, FontSize = 12, PagePadding = new Thickness(0), PageWidth = Math.Max(100, width + 24), TextAlignment = TextAlignment.Left,
        };
        var body = new RichTextBox
        {
            Document = document, IsReadOnly = true, IsDocumentEnabled = false, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Padding = new Thickness(4, 6, 8, 6),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        body.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        ForwardMouseWheel(body);
        return body;
    }

    /// <summary>
    /// 只水平滚动的内嵌控件（代码块、表格）会吞掉鼠标滚轮，鼠标停在上面时对话无法上下滚动：把滚轮事件转交给父元素，由外层对话的滚动区域处理。
    /// </summary>
    private static void ForwardMouseWheel(UIElement element) => WheelForwarding.Attach(element);

    /// <summary>当前 VS 主题（深 / 浅）对应的代码着色画刷。</summary>
    internal static IReadOnlyDictionary<CodeTokenKind, Brush> CurrentPalette => IsDarkTheme() ? DarkPalette : LightPalette;

    private static bool IsDarkTheme()
    {
        var color = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
        return (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255 < 0.5;
    }

    // 与 VS 默认深色 / 浅色主题的编辑器配色接近。
    private static readonly IReadOnlyDictionary<CodeTokenKind, Brush> DarkPalette = Palette(
        "#569CD6", "#4EC9B0", "#D69D85", "#57A64A", "#B5CEA8", "#9B9B9B", "#569CD6", "#9CDCFE");
    private static readonly IReadOnlyDictionary<CodeTokenKind, Brush> LightPalette = Palette(
        "#0000FF", "#2B91AF", "#A31515", "#008000", "#098658", "#808080", "#800000", "#E50000");

    private static IReadOnlyDictionary<CodeTokenKind, Brush> Palette(params string[] colors)
    {
        var kinds = new[] { CodeTokenKind.Keyword, CodeTokenKind.Type, CodeTokenKind.String, CodeTokenKind.Comment, CodeTokenKind.Number,
            CodeTokenKind.Preprocessor, CodeTokenKind.Tag, CodeTokenKind.Attribute };
        return kinds.Select((kind, i) => (kind, brush: Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i])))))
            .ToDictionary(p => p.kind, p => p.brush);
    }

    private static Brush Freeze(Brush brush) { brush.Freeze(); return brush; }
}

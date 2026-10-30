using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace SLCopilot;

/// <summary>
/// 聊天窗口视图。负责只能在视图层完成的交互：自动滚动到底部、Enter 发送、空状态与占位提示。
/// </summary>
public partial class ChatControl : UserControl
{
    private readonly ChatViewModel viewModel;
    // 用户停留在底部时跟随新内容；手动向上滚动后停止跟随，回到底部后恢复。
    private bool followTail = true;

    public ChatControl(ChatViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Messages.CollectionChanged += OnMessagesChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += (_, _) =>
        {
            viewModel.Messages.CollectionChanged -= OnMessagesChanged;
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        };
        Loaded += (_, _) =>
        {
            viewModel.Messages.CollectionChanged -= OnMessagesChanged;
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.Messages.CollectionChanged += OnMessagesChanged;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateHints();
        };
        UpdateHints();
        // 粘贴：剪贴板中有图片（截图、复制的图片文件）时附加图片而不是粘贴文字；纯图片时 TextBox 默认禁用粘贴，这里放开。
        CommandManager.AddPreviewCanExecuteHandler(PromptBox, (_, e) =>
        {
            if (e.Command != ApplicationCommands.Paste || !ImageCapture.ClipboardHasImage()) return;
            e.CanExecute = true;
            e.Handled = true;
        });
        CommandManager.AddPreviewExecutedHandler(PromptBox, (_, e) =>
        {
            if (e.Command == ApplicationCommands.Paste && viewModel.TryPasteImage()) e.Handled = true;
        });
        // 在控件级处理 Esc：运行中任意位置按 Esc 停止（与 Claude Code 一致）；空闲时关闭命令与文件提示列表。
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (!viewModel.IsBusy)
            {
                if (viewModel.DismissSuggestions()) e.Handled = true;
                return;
            }
            if (!viewModel.CancelCommand.CanExecute(null)) return;
            e.Handled = true;
            viewModel.CancelCommand.Execute(null);
        };
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            // 新消息（发送或开始回答）时总是回到底部，与 Copilot 一致。
            followTail = true;
            Scroller.ScrollToEnd();
        }
        UpdateHints();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.IsBusy))
        {
            // 输入框随运行禁用后焦点会离开工具窗口；移到“停止”按钮上，保证 Esc 可用；结束后回到输入框。
            // 等绑定更新可见性与启用状态后再移动焦点。
            Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await System.Threading.Tasks.Task.Yield();
                if (viewModel.IsBusy) StopButton.Focus();
                else if (IsKeyboardFocusWithin) PromptBox.Focus();
            }).FileAndForget("SLCopilot/focus");
            return;
        }
        if (e.PropertyName != nameof(ChatViewModel.Prompt)) return;
        UpdateHints();
        // 点击斜杠命令后把焦点放回输入框末尾，便于继续输入。
        if (!PromptBox.IsKeyboardFocusWithin && viewModel.Prompt.StartsWith("/", StringComparison.Ordinal))
        {
            PromptBox.Focus();
            PromptBox.CaretIndex = PromptBox.Text.Length;
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0)
            followTail = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 4;
        else if (followTail)
            Scroller.ScrollToEnd();
        JumpToLatest.Visibility = followTail || Scroller.ScrollableHeight <= 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnJumpToLatest(object sender, RoutedEventArgs e)
    {
        followTail = true;
        Scroller.ScrollToEnd();
        JumpToLatest.Visibility = Visibility.Collapsed;
    }

    /// <summary>拖入图片文件时显示“复制”光标；其他内容交给输入框默认处理（拖入文字）。</summary>
    private void OnPromptDragOver(object sender, DragEventArgs e)
    {
        if (DroppedFiles(e).Length == 0) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    /// <summary>拖入图片时附加图片；拖入其他文件（资源管理器、解决方案资源管理器）时插入 #file 引用。</summary>
    private void OnPromptDrop(object sender, DragEventArgs e)
    {
        var files = DroppedFiles(e);
        if (files.Length == 0) return;
        e.Handled = true;
        if (viewModel.IsBusy) return;
        var images = files.Where(ImageCapture.IsImageFile).ToList();
        if (images.Count > 0) viewModel.AddImageFiles(images);
        viewModel.ReferenceFiles(files.Where(f => !ImageCapture.IsImageFile(f)));
        if (sender is TextBox box) box.CaretIndex = box.Text.Length;
    }

    private static string[] DroppedFiles(DragEventArgs e)
        => e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files ? files.Where(System.IO.File.Exists).ToArray() : [];

    /// <summary>输入框获得焦点时刷新“将附带的编辑器上下文”提示。</summary>
    private void OnPromptFocused(object sender, KeyboardFocusChangedEventArgs e) => viewModel.RefreshEditorHint();

    private void OnPromptKeyDown(object sender, KeyEventArgs e)
    {
        // 有命令或文件提示时：↑ / ↓ 移动选中项，Tab / Enter 接受（输入已是完整命令时 Enter 直接发送）。
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Up or Key.Down or Key.Tab or Key.Enter && sender is TextBox suggesting
            && viewModel.HandleSuggestionKey(e.Key switch { Key.Up => -1, Key.Down => 1, _ => 0 }))
        {
            e.Handled = true;
            suggesting.CaretIndex = suggesting.Text.Length;
            return;
        }
        // ↑ 在第一行、↓ 在最后一行时切换之前发送过的输入；其他情况按默认方式移动光标。
        if (e.Key is Key.Up or Key.Down && Keyboard.Modifiers == ModifierKeys.None && sender is TextBox box && box.SelectionLength == 0)
        {
            var line = box.GetLineIndexFromCharacterIndex(box.CaretIndex);
            var atEdge = e.Key == Key.Up ? line <= 0 : line < 0 || line >= box.LineCount - 1;
            if (atEdge && viewModel.RecallPrompt(older: e.Key == Key.Up))
            {
                e.Handled = true;
                box.CaretIndex = box.Text.Length;
            }
            return;
        }
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        e.Handled = true;
        if (viewModel.IsBusy)
        {
            viewModel.Steer();
            return;
        }
        if (!viewModel.IsBusy && viewModel.SendCommand.CanExecute(null)) viewModel.SendCommand.Execute(null);
    }

    /// <summary>
    /// 模型菜单（与 Claude Code 的模型菜单相同的结构）：模型列表（当前项打勾，前四项可按数字键）、思考强度子菜单、更多模型子菜单。
    /// 每次点击时按当前配置重新生成。
    /// </summary>
    private void OnModelButtonClick(object sender, RoutedEventArgs e)
    {
        if (viewModel.IsBusy) return;
        var data = viewModel.GetModelMenu();
        var menu = new ContextMenu { PlacementTarget = ModelButton, Placement = PlacementMode.Top, MinWidth = 260 };
        menu.SetResourceReference(BackgroundProperty, EnvironmentColors.CommandBarMenuBackgroundGradientBrushKey);
        menu.SetResourceReference(ForegroundProperty, EnvironmentColors.CommandBarTextActiveBrushKey);
        var shortcuts = new List<ModelChoice>();
        foreach (var choice in data.Main)
        {
            var item = Item(choice.Label, choice.IsCurrent, () => viewModel.SelectModel(choice));
            if (shortcuts.Count < 4) { shortcuts.Add(choice); item.InputGestureText = shortcuts.Count.ToString(CultureInfo.InvariantCulture); }
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var current = data.Efforts.FirstOrDefault(c => c.IsCurrent);
        var effort = new MenuItem { Header = data.EffortTitle, InputGestureText = current is null ? "" : Core.ModelNames.EffortLabel(current.Value) };
        foreach (var choice in data.Efforts) effort.Items.Add(Item(choice.Label, choice.IsCurrent, () => viewModel.SelectEffort(choice.Value)));
        menu.Items.Add(effort);
        var more = new MenuItem { Header = "更多模型" };
        foreach (var choice in data.More) more.Items.Add(Item(choice.Label, choice.IsCurrent, () => viewModel.SelectModel(choice)));
        if (data.More.Count > 0) more.Items.Add(new Separator());
        more.Items.Add(Item("从服务商获取模型列表…", false, () =>
        {
            if (viewModel.RefreshModelsCommand.CanExecute(null)) viewModel.RefreshModelsCommand.Execute(null);
        }));
        menu.Items.Add(more);
        menu.PreviewKeyDown += (_, args) =>
        {
            var index = args.Key - Key.D1;
            if (args.Key is >= Key.NumPad1 and <= Key.NumPad4) index = args.Key - Key.NumPad1;
            if (index < 0 || index >= shortcuts.Count) return;
            args.Handled = true;
            menu.IsOpen = false;
            viewModel.SelectModel(shortcuts[index]);
        };
        menu.IsOpen = true;

        static MenuItem Item(string header, bool isChecked, Action action)
        {
            var item = new MenuItem { Header = header, IsCheckable = false, IsChecked = isChecked };
            item.Click += (_, _) => action();
            return item;
        }
    }

    // 密钥只在点击“保存密钥”时从密码框读取一次，不绑定到视图模型属性。
    private void OnSaveKeyClick(object sender, RoutedEventArgs e)
    {
        if (viewModel.SaveApiKey(KeyBox.Password)) KeyBox.Clear();
    }

    private void UpdateHints()
    {
        EmptyHint.Visibility = viewModel.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PromptHint.Visibility = string.IsNullOrEmpty(viewModel.Prompt) ? Visibility.Visible : Visibility.Collapsed;
    }
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public static readonly InverseBooleanConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class InverseVisibilityConverter : IValueConverter
{
    public static readonly InverseVisibilityConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public static readonly NotNullToVisibilityConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

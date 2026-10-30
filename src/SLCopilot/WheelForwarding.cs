using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SLCopilot;

/// <summary>
/// 内含滚动区域的只读控件（可选中的文本框、代码块、表格）会吞掉鼠标滚轮，鼠标停在上面时对话无法上下滚动。
/// 设置 <c>WheelForwarding.Enabled="True"</c> 后把滚轮事件转交给父元素，由外层对话的滚动区域处理。
/// </summary>
public static class WheelForwarding
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(WheelForwarding), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.PreviewMouseWheel -= Forward;
        if (e.NewValue is true) element.PreviewMouseWheel += Forward;
    }

    /// <summary>在代码中启用转交（与在 XAML 中设置 Enabled 相同）。</summary>
    public static void Attach(UIElement element) => SetEnabled(element, true);

    private static void Forward(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject source || VisualTreeHelper.GetParent(source) is not UIElement parent) return;
        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sender });
    }
}

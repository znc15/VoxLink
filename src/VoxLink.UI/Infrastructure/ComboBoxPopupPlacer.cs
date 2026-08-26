using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace VoxLink.UI.Infrastructure;

/// <summary>
/// 修复 WinUI 3 ComboBox 下拉弹层定位：模板 Popup 偏移恒为 0，实际按
/// 「选中项对齐控件」定位，选中项靠后时弹层整体翻到控件上方盖住界面。
/// 页面 Loaded 时调 <see cref="Apply"/>（对整个页面的可视树生效，
/// WinUI 3 XAML 编译路径下自定义附加属性不可靠，故用代码挂接）。
/// 通过 Popup.Opened（内容布局完成后）立即改按「控件底边 + 2px」对齐：
/// 不隐藏内容、不等动画结束，展开动画从正确位置照常播放。
/// </summary>
public static class ComboBoxPopupPlacer
{
    /// <summary>对 root 可视树内所有 ComboBox（含动态创建前已存在的）挂接纠偏。</summary>
    public static void Apply(FrameworkElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.Loaded -= OnRootLoaded;
        root.Loaded += OnRootLoaded;
        HookAll(root);
    }

    private static void OnRootLoaded(object sender, RoutedEventArgs args) =>
        HookAll((FrameworkElement)sender);

    private static void HookAll(FrameworkElement root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(root, i) is not { } child)
            {
                continue;
            }

            if (child is ComboBox comboBox)
            {
                HookOne(comboBox);
            }
            else if (child is FrameworkElement childElement)
            {
                HookAll(childElement);
            }
        }
    }

    private static void HookOne(ComboBox comboBox)
    {
        comboBox.DropDownOpened -= OnDropDownOpened;
        comboBox.DropDownOpened += OnDropDownOpened;
    }

    private static void OnDropDownOpened(object? sender, object args)
    {
        if (sender is not ComboBox comboBox)
        {
            return;
        }

        var popup = FindTemplatePopup(comboBox);
        if (popup is null)
        {
            return;
        }

        // Popup 每次打开内容重新布局：直接纠偏（DropDownOpened 触发时
        // 弹层布局已完成，动画只是视觉过渡，不影响 TransformToVisual 布局值）。
        CorrectPopup(popup, comboBox);
    }

    private static void CorrectPopup(Popup popup, ComboBox comboBox)
    {
        if (popup.Child is not FrameworkElement content)
        {
            return;
        }

        try
        {
            // 弹层内容顶部相对 ComboBox 顶部的距离（负值 = 翻到了上方）。
            // TransformToVisual 测布局位置（已含 VerticalOffset），直接补差。
            var top = content.TransformToVisual(comboBox).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            var desired = comboBox.ActualHeight + 2;
            var delta = desired - top;
            if (Math.Abs(delta) > 0.5)
            {
                popup.VerticalOffset += delta;
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            // 元素已收起或已从树中移除时忽略
        }
    }

    private static Popup? FindTemplatePopup(FrameworkElement root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(root, i) is { } child)
            {
                if (child is Popup popup)
                {
                    return popup;
                }

                if (child is FrameworkElement childElement
                    && FindTemplatePopup(childElement) is { } nested)
                {
                    return nested;
                }
            }
        }

        return null;
    }
}

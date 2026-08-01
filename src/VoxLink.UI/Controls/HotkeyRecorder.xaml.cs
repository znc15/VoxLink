using System.Text;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace VoxLink.UI.Controls;

/// <summary>
/// 点击后进入「录制」态，捕获下一次按下的组合键（修饰键 + 主键），
/// 生成与 Engine <c>GlobalHotkeyService.Parse</c> 兼容的字符串（WPF Key 枚举名）。
/// 仅在按下至少一个修饰键与一个主键时才提交，从源头避免「只有修饰键」的非法值。
/// </summary>
public sealed partial class HotkeyRecorder : UserControl
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey),
        typeof(string),
        typeof(HotkeyRecorder),
        new PropertyMetadata(string.Empty, OnHotkeyChanged));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(HotkeyRecorder),
        new PropertyMetadata(string.Empty, OnTitleChanged));

    private bool _isRecording;
    private Brush? _defaultBorder;

    public HotkeyRecorder()
    {
        InitializeComponent();
        ProtectedCursor = InputCursor.CreateFromCoreCursor(new CoreCursor(CoreCursorType.Hand, 1));
    }

    public string Hotkey
    {
        get => (string)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    private static void OnHotkeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((HotkeyRecorder)d).RenderDisplay();

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (HotkeyRecorder)d;
        control.HeaderText.Visibility = string.IsNullOrWhiteSpace(control.Title)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void Surface_Tapped(object sender, TappedRoutedEventArgs e) => ToggleRecording();

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        if (_defaultBorder is null)
        {
            _defaultBorder = Surface.BorderBrush;
        }
    }

    private void OnLostFocus(object sender, RoutedEventArgs e) => EndRecording(commit: false);

    private void ToggleRecording()
    {
        if (_isRecording)
        {
            EndRecording(commit: false);
        }
        else
        {
            BeginRecording();
        }
    }

    private void BeginRecording()
    {
        if (_isRecording)
        {
            return;
        }

        _isRecording = true;
        _defaultBorder ??= Surface.BorderBrush;
        DisplayPanel.Visibility = Visibility.Collapsed;
        RecordingPanel.Visibility = Visibility.Visible;
        RecordingHint.Text = "按下组合键…（Esc 取消）";
        Surface.BorderBrush = (Brush)Application.Current.Resources["VoxLinkAccentBrush"];
        _ = Focus(FocusState.Programmatic);
    }

    private void EndRecording(bool commit)
    {
        if (!_isRecording)
        {
            return;
        }

        _isRecording = false;
        RecordingPanel.Visibility = Visibility.Collapsed;
        DisplayPanel.Visibility = Visibility.Visible;
        Surface.BorderBrush = _defaultBorder;
        if (!commit)
        {
            RenderDisplay();
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isRecording)
        {
            if (e.Key is VirtualKey.Enter or VirtualKey.Space)
            {
                BeginRecording();
                e.Handled = true;
            }

            return;
        }

        e.Handled = true;
        var key = e.Key;

        if (key == VirtualKey.Escape)
        {
            EndRecording(commit: false);
            return;
        }

        if (IsModifierKey(key))
        {
            var held = DescribeHeldModifiers();
            RecordingHint.Text = (held.Length == 0 ? "" : held + " + ")
                + "再按一个主键…（Esc 取消）";
            return;
        }

        var keyName = ToHotkeyKeyName(key);
        if (keyName is null)
        {
            RecordingHint.Text = "不支持该按键，请换一个…";
            return;
        }

        var modifiers = BuildModifiers();
        if (modifiers.Length == 0)
        {
            RecordingHint.Text = "需要至少一个修饰键（Ctrl / Alt / Shift）…";
            return;
        }

        Hotkey = modifiers + keyName;
        EndRecording(commit: true);
    }

    private void RenderDisplay()
    {
        DisplayPanel.Children.Clear();
        var value = Hotkey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            DisplayPanel.Children.Add(MakeText("未设置（点击录制）", secondary: true));
            return;
        }

        if (!IsValidHotkey(value))
        {
            DisplayPanel.Children.Add(MakeText(value + "（无效，点击重新录制）", warning: true));
            return;
        }

        var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            DisplayPanel.Children.Add(MakeChip(DisplayName(part)));
        }
    }

    private static TextBlock MakeText(string text, bool secondary = false, bool warning = false)
    {
        var key = warning
            ? "VoxLinkCriticalBrush"
            : secondary
                ? "TextFillColorSecondaryBrush"
                : "TextFillColorPrimaryBrush";
        return new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = (Brush)Application.Current.Resources[key],
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        };
    }

    private static Border MakeChip(string label)
    {
        var background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        var border = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];
        return new Border
        {
            Padding = new Thickness(8, 3, 8, 3),
            Background = background,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = new TextBlock
            {
                Text = label,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private static bool IsModifierKey(VirtualKey key) =>
        key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
            or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
            or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
            or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    /// <summary>把 WinUI VirtualKey 映射为 Engine 解析器接受的 WPF <c>Key</c> 枚举名。</summary>
    private static string? ToHotkeyKeyName(VirtualKey key)
    {
        if (key >= VirtualKey.A && key <= VirtualKey.Z)
        {
            return key.ToString();
        }

        if (key >= VirtualKey.F1 && key <= VirtualKey.F24)
        {
            return key.ToString();
        }

        if (key >= VirtualKey.Number0 && key <= VirtualKey.Number9)
        {
            return "D" + (int)(key - VirtualKey.Number0);
        }

        return key switch
        {
            VirtualKey.Space => "Space",
            VirtualKey.Enter => "Enter",
            VirtualKey.Tab => "Tab",
            VirtualKey.Insert => "Insert",
            VirtualKey.Delete => "Delete",
            VirtualKey.Home => "Home",
            VirtualKey.End => "End",
            VirtualKey.PageUp => "PageUp",
            VirtualKey.PageDown => "PageDown",
            VirtualKey.Left => "Left",
            VirtualKey.Right => "Right",
            VirtualKey.Up => "Up",
            VirtualKey.Down => "Down",
            _ => null
        };
    }

    private static string BuildModifiers()
    {
        var builder = new StringBuilder();
        if (IsDown(VirtualKey.Control))
        {
            builder.Append("Ctrl+");
        }

        if (IsDown(VirtualKey.Menu))
        {
            builder.Append("Alt+");
        }

        if (IsDown(VirtualKey.Shift))
        {
            builder.Append("Shift+");
        }

        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows))
        {
            builder.Append("Win+");
        }

        return builder.ToString();
    }

    private static string DescribeHeldModifiers() => BuildModifiers().TrimEnd('+').Replace("+", " + ");

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static string DisplayName(string part)
    {
        var trimmed = part.Trim();
        return trimmed.ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => "Ctrl",
            "ALT" => "Alt",
            "SHIFT" => "Shift",
            "WIN" or "WINDOWS" => "Win",
            "SPACE" => "Space",
            "ENTER" => "Enter",
            "TAB" => "Tab",
            var digit when digit.Length == 2 && digit[0] == 'D' && char.IsDigit(digit[1]) => digit[1].ToString(),
            _ => trimmed
        };
    }

    private static bool IsValidHotkey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var hasModifier = false;
        var hasMain = false;
        foreach (var part in parts)
        {
            if (IsModifierToken(part))
            {
                hasModifier = true;
            }
            else
            {
                hasMain = true;
            }
        }

        return hasModifier && hasMain;
    }

    private static bool IsModifierToken(string part) => part.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" or "ALT" or "SHIFT" or "WIN" or "WINDOWS" => true,
        _ => false
    };
}

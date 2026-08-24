using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using VoxLink.Models;

namespace VoxLink;

public partial class OverlayWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExNoactivate = 0x08000000;
    private const double DefaultWidth = 760;
    private readonly DispatcherTimer _hideTimer;
    private bool _enabled = true;
    private bool _lockPosition = true;
    private bool _hasSavedPlacement;

    public OverlayWindow()
    {
        InitializeComponent();
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>位置或尺寸变化；height 仅在窗口处于手动高度（拉伸过）时非空。</summary>
    public event Action<double, double, double, double?>? PlacementChanged;

    public void Configure(
        double? left,
        double? top,
        double? width,
        double? height,
        int fontSize,
        bool topmost,
        bool lockPosition)
    {
        _lockPosition = lockPosition;
        Topmost = topmost;
        ApplyFontSize(fontSize);
        if (left is null && top is null && width is null && height is null)
        {
            // 全空位置表示「重置位置与大小」：恢复默认宽度与自动高度，
            // 清除保存标记后回到主屏底部居中（可见时立即生效，否则下次显示时定位）。
            _hasSavedPlacement = false;
            SizeToContent = System.Windows.SizeToContent.Height;
            Width = DefaultWidth;
            if (IsVisible)
            {
                UpdateLayout();
                PositionAtBottom();
            }
        }
        else
        {
            if (left is not null)
            {
                Left = left.Value;
            }

            if (top is not null)
            {
                Top = top.Value;
            }

            if (width is > 0)
            {
                Width = width.Value;
            }

            if (height is not null)
            {
                SizeToContent = System.Windows.SizeToContent.Manual;
                Height = Math.Clamp(height.Value, MinHeight, 2000);
            }
            else
            {
                SizeToContent = System.Windows.SizeToContent.Height;
            }

            _hasSavedPlacement = left is not null && top is not null;
        }

        ResizeThumb.Visibility = lockPosition ? Visibility.Collapsed : Visibility.Visible;
        UpdateTransparency();
    }

    public void ShowSubtitle(ConversationMessage message)
    {
        if (!_enabled)
        {
            return;
        }

        HeaderTextBlock.Text = message.HeaderLabel;
        TranslatedTextBlock.Text = message.PrimaryDisplayText;
        SecondaryTextBlock.Text = message.SecondaryDisplayText;
        SecondaryTextBlock.Visibility = string.IsNullOrWhiteSpace(message.SecondaryDisplayText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        SourceTextBlock.Text = message.SourceDisplayText;
        SourceTextBlock.Visibility = string.IsNullOrWhiteSpace(message.SourceDisplayText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (!IsVisible)
        {
            if (_hasSavedPlacement)
            {
                ClampToVirtualScreen();
            }
            Show();
            UpdateLayout();
            if (!_hasSavedPlacement)
            {
                PositionAtBottom();
            }
        }
        else
        {
            UpdateLayout();
        }
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled)
        {
            _hideTimer.Stop();
            Hide();
        }
    }

    public string ShowTest()
    {
        if (!_enabled)
        {
            return "桌面字幕已关闭，请先开启桌面字幕悬浮窗。";
        }

        ShowSubtitle(new ConversationMessage(
            TranslationDirection.Inbound,
            "VoxLink 桌面字幕测试",
            "桌面字幕显示正常",
            DateTimeOffset.Now));
        return "桌面字幕测试已显示";
    }

    private void ApplyFontSize(int fontSize)
    {
        var primary = Math.Clamp(fontSize, 14, 40);
        TranslatedTextBlock.FontSize = primary;
        // 次译文≈主字号×0.7、原文≈主字号×0.58，按设计比例取整联动。
        SecondaryTextBlock.FontSize = Math.Max(10, (int)Math.Round(primary * 0.7));
        SourceTextBlock.FontSize = Math.Max(8, (int)Math.Round(primary * 0.58));
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLongPtr(handle, GwlExstyle).ToInt64();
        SetWindowLongPtr(
            handle,
            GwlExstyle,
            new IntPtr(extendedStyle | WsExTransparent | WsExToolwindow | WsExNoactivate));
        UpdateTransparency();
    }

    private void UpdateTransparency()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var extendedStyle = GetWindowLongPtr(handle, GwlExstyle).ToInt64();
        extendedStyle = _lockPosition
            ? extendedStyle | WsExTransparent
            : extendedStyle & ~WsExTransparent;
        SetWindowLongPtr(handle, GwlExstyle, new IntPtr(extendedStyle));
    }

    private void PositionAtBottom()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + ((workArea.Width - Width) / 2);
        Top = workArea.Bottom - ActualHeight - 54;
    }

    /// <summary>
    /// 显示前把保存的位置收敛到虚拟屏幕范围内，
    /// 避免分辨率变化或显示器移除后窗口完全丢失在屏幕外。
    /// </summary>
    private void ClampToVirtualScreen()
    {
        Left = Math.Clamp(
            Left,
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenLeft + Math.Max(0, SystemParameters.VirtualScreenWidth - MinWidth));
        Top = Math.Clamp(
            Top,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenTop + Math.Max(0, SystemParameters.VirtualScreenHeight - MinHeight));
    }

    private void OverlayWindow_MouseLeftButtonDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs eventArgs)
    {
        if (_lockPosition || eventArgs.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
            RaisePlacementChanged();
        }
        catch (InvalidOperationException)
        {
            // 拖动过程中窗口可能被外部隐藏，忽略即可。
        }
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs eventArgs)
    {
        if (_lockPosition)
        {
            return;
        }

        if (SizeToContent == System.Windows.SizeToContent.Height)
        {
            SizeToContent = System.Windows.SizeToContent.Manual;
            Height = ActualHeight;
        }

        Width = Math.Max(MinWidth, Width + eventArgs.HorizontalChange);
        Height = Math.Max(MinHeight, Height + eventArgs.VerticalChange);
    }

    private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs eventArgs) =>
        RaisePlacementChanged();

    private void RaisePlacementChanged() => PlacementChanged?.Invoke(
        Left,
        Top,
        Width,
        SizeToContent == System.Windows.SizeToContent.Manual ? Height : null);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newLong);
}

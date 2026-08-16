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

    public event Action<double, double, double>? PlacementChanged;

    public void Configure(
        double? left,
        double? top,
        double? width,
        bool topmost,
        bool lockPosition)
    {
        _lockPosition = lockPosition;
        Topmost = topmost;
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

        _hasSavedPlacement = left is not null && top is not null;
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

    private void RaisePlacementChanged() => PlacementChanged?.Invoke(Left, Top, Width);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newLong);
}

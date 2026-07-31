using System.Runtime.InteropServices;
using System.Windows;
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
        }

        UpdateLayout();
        PositionAtBottom();
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

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLongPtr(handle, GwlExstyle).ToInt64();
        SetWindowLongPtr(
            handle,
            GwlExstyle,
            new IntPtr(extendedStyle | WsExTransparent | WsExToolwindow | WsExNoactivate));
    }

    private void PositionAtBottom()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + ((workArea.Width - Width) / 2);
        Top = workArea.Bottom - ActualHeight - 54;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newLong);
}

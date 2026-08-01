using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VoxLink.UI.Controls;
using VoxLink.UI.Core.Models;
using VoxLink.UI.Core.Services;
using VoxLink.UI.Pages;
using Windows.Graphics;

namespace VoxLink.UI;

public sealed partial class MainWindow : Window
{
    private bool _allowClose;
    private bool _closeRequested;
    private bool _onboardingOpen;
    private bool _onboardingPending;
    private AppSettings? _subscribedSettings;
    private MicaBackdrop? _micaBackdrop;

    // 拖拽卡顿诊断：统计窗口位置更新之间的间隔，判断卡顿来自 UI 线程还是合成器。
    private bool _dragDiagOn;
    private int _dragPosSamples;
    private long _lastDragTimestampTicks;
    private double _dragMaxGapMs;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new SizeInt32(1280, 800));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 640;
            presenter.PreferredMinimumHeight = 560;
        }

        AppWindow.Closing += AppWindow_Closing;
        AppWindow.Changed += AppWindow_Changed;
        RootLayout.Loaded += RootLayout_Loaded;
        App.Controller.PropertyChanged += Controller_PropertyChanged;
        App.Controller.OnboardingRequested += Controller_OnboardingRequested;
        EnsureSettingsSubscribed();
        LogService.Instance.Info(
            "UI",
            $"窗口已创建：ExtendsContentIntoTitleBar={ExtendsContentIntoTitleBar}，最小尺寸 {AppWindow.Size.Width}x{AppWindow.Size.Height}。");
        LogService.Instance.Info("UI", "显示器：" + DescribePrimaryDisplay());
        ContentFrame.Navigate(typeof(LivePage));
        UpdateEngineStatus();
    }

    private void EnsureSettingsSubscribed()
    {
        var settings = App.Controller.Settings;
        if (ReferenceEquals(_subscribedSettings, settings))
        {
            return;
        }

        if (_subscribedSettings is not null)
        {
            _subscribedSettings.PropertyChanged -= Settings_PropertyChanged;
        }

        _subscribedSettings = settings;
        settings.PropertyChanged += Settings_PropertyChanged;
        ApplyWindowChrome();
        UpdateDragDiag(settings.DiagnoseDragPerformance);
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!_dragDiagOn || !args.DidPositionChange)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (_lastDragTimestampTicks != 0)
        {
            var gapMs = (now - _lastDragTimestampTicks) * 1000.0 / Stopwatch.Frequency;
            if (gapMs > _dragMaxGapMs)
            {
                _dragMaxGapMs = gapMs;
            }
        }

        _lastDragTimestampTicks = now;
        _dragPosSamples++;
        if (_dragPosSamples >= 30)
        {
            LogService.Instance.Info(
                "UI",
                $"拖拽采样：{_dragPosSamples} 次位置更新，最大间隔 {_dragMaxGapMs:F0}ms（≈16ms 为顺畅；越大越卡；几乎不更新=合成器/系统层）。");
            _dragPosSamples = 0;
            _dragMaxGapMs = 0;
        }
    }

    private void UpdateDragDiag(bool enabled)
    {
        _dragDiagOn = enabled;
        _dragPosSamples = 0;
        _dragMaxGapMs = 0;
        _lastDragTimestampTicks = 0;
        if (enabled)
        {
            LogService.Instance.Info("UI", "拖拽诊断已开启：拖动窗口 3~5 秒，然后查看日志中的「拖拽采样」行。");
        }
    }

    /// <summary>
    /// 应用窗口外观：Mica 透明背景 与 自定义/系统标题栏。两者都是排查「拖拽慢半拍」的开关——
    /// Mica 影响移动/拉伸时的背景重采样，自定义标题栏影响拖拽命中区由谁计算。
    /// </summary>
    private void ApplyWindowChrome()
    {
        var settings = App.Controller.Settings;
        if (settings.UseMicaBackdrop)
        {
            SystemBackdrop = _micaBackdrop ??= new MicaBackdrop();
            RootLayout.Background = null;
        }
        else
        {
            SystemBackdrop = null;
            RootLayout.Background = Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] as Brush;
        }

        var useSystemTitleBar = settings.UseSystemTitleBar;
        ExtendsContentIntoTitleBar = !useSystemTitleBar;
        AppTitleBar.Visibility = useSystemTitleBar ? Visibility.Collapsed : Visibility.Visible;
        NavView.IsPaneToggleButtonVisible = useSystemTitleBar;
        if (!useSystemTitleBar)
        {
            SetTitleBar(AppTitleBar);
        }
    }

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AppSettings.UseMicaBackdrop) or nameof(AppSettings.UseSystemTitleBar))
        {
            ApplyWindowChrome();
            LogService.Instance.Info(
                "UI",
                $"窗口外观已更新：Mica={App.Controller.Settings.UseMicaBackdrop}，系统标题栏={App.Controller.Settings.UseSystemTitleBar}（标题栏模式如显示异常请重启）。");
        }

        if (args.PropertyName == nameof(AppSettings.DiagnoseDragPerformance))
        {
            UpdateDragDiag(App.Controller.Settings.DiagnoseDragPerformance);
        }
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args) =>
        NavView.IsPaneOpen = !NavView.IsPaneOpen;

    private void NavView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        var pageType = tag switch
        {
            "live" => typeof(LivePage),
            "providers" => typeof(ProvidersPage),
            "audio" => typeof(AudioPage),
            "vrchat" => typeof(VRChatPage),
            "advanced" => typeof(AdvancedPage),
            "logs" => typeof(LogsPage),
            _ => typeof(LivePage)
        };
        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }

    private void Controller_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(App.Controller.EngineConnected)
            or nameof(App.Controller.StatusMessage))
        {
            UpdateEngineStatus();
        }

        if (args.PropertyName == nameof(App.Controller.Settings))
        {
            EnsureSettingsSubscribed();
        }
    }

    private void Controller_OnboardingRequested(object? sender, EventArgs args)
    {
        _onboardingPending = true;
        DispatcherQueue.TryEnqueue(async () => await TryShowOnboardingAsync());
    }

    private async void RootLayout_Loaded(object sender, RoutedEventArgs args) =>
        await TryShowOnboardingAsync();

    public async Task ShowOnboardingAsync()
    {
        _onboardingPending = true;
        await TryShowOnboardingAsync();
    }

    private async Task TryShowOnboardingAsync()
    {
        if (!_onboardingPending || _onboardingOpen || RootLayout.XamlRoot is null)
        {
            return;
        }

        _onboardingPending = false;
        _onboardingOpen = true;
        try
        {
            var dialog = new OnboardingDialog(App.Controller)
            {
                XamlRoot = RootLayout.XamlRoot
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _onboardingOpen = false;
        }
    }

    private void UpdateEngineStatus()
    {
        var connected = App.Controller.EngineConnected;
        EngineStatusText.Text = connected ? "本地引擎已连接" : App.Controller.StatusMessage;
        EngineConnectedDot.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        EngineWarningDot.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        if (_closeRequested)
        {
            return;
        }

        _closeRequested = true;
        try
        {
            await App.Controller.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (Exception exception)
        {
            LogService.Instance.Error("UI", exception, "关闭流程异常");
            System.Diagnostics.Debug.WriteLine($"VoxLink shutdown failed: {exception}");
        }
        finally
        {
            LogService.Instance.Info("UI", "VoxLink 即将退出。");
            _allowClose = true;
            RootLayout.Loaded -= RootLayout_Loaded;
            App.Controller.PropertyChanged -= Controller_PropertyChanged;
            App.Controller.OnboardingRequested -= Controller_OnboardingRequested;
            Close();
        }
    }

    /// <summary>读取主显示器刷新率与分辨率——判断拖拽迟滞是否源于高刷新率面板（WinUI3 常被限制在~60Hz）。/</summary>
    private static string DescribePrimaryDisplay()
    {
        try
        {
            var hdc = GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero)
            {
                return "无法获取显示器 DC。";
            }

            try
            {
                var refresh = GetDeviceCaps(hdc, VREFRESH);
                var width = GetDeviceCaps(hdc, DESKTOPHORZRES);
                var height = GetDeviceCaps(hdc, DESKTOPVERTRES);
                var bits = GetDeviceCaps(hdc, BITSPIXEL);
                var monitors = GetSystemMetrics(SM_CMONITORS);
                var hint = refresh >= 100
                    ? "（高刷新率：WinUI3 拖拽可能被限制在~60Hz，看起来会比原生窗口迟滞）"
                    : string.Empty;
                return $"{width}x{height}@{refresh}Hz，{bits}bpp，监视器数 {monitors}{hint}";
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdc);
            }
        }
        catch (Exception exception)
        {
            return "显示器信息获取失败：" + exception.Message;
        }
    }

    private const int VREFRESH = 116;
    private const int DESKTOPHORZRES = 118;
    private const int DESKTOPVERTRES = 117;
    private const int BITSPIXEL = 12;
    private const int SM_CMONITORS = 80;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>
    /// 打开一个几乎空白的 WinUI3 窗口（系统标题栏、无 Mica、无内容、不依赖引擎），
    /// 用于在高刷新率屏上判定拖拽迟滞究竟来自 WinUI3 平台还是 VoxLink 主窗口内容。
    /// </summary>
    public static void OpenDragProbe()
    {
        if (_dragProbe is not null)
        {
            _dragProbe.Activate();
            return;
        }

        var probe = new Window
        {
            Title = "VoxLink 拖拽测试（空白 WinUI3 窗口）",
            ExtendsContentIntoTitleBar = false,
            SystemBackdrop = null
        };
        var grid = new Grid
        {
            Padding = new Thickness(28),
            Background = Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] as Brush
        };
        grid.Children.Add(new TextBlock
        {
            Text = "这是一个几乎空白的 WinUI3 窗口：系统标题栏、无 Mica、无业务内容。\n\n拖动它的标题栏与主窗口对比：\n• 同样迟滞 → WinUI3 平台在高刷新率屏上的限制，与 VoxLink 代码无关。\n• 更顺滑 → 主窗口内容的问题，我去优化。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Foreground = Application.Current.Resources["TextFillColorPrimaryBrush"] as Brush
        });
        probe.Content = grid;
        probe.AppWindow.Resize(new SizeInt32(680, 320));
        probe.Closed += (_, _) => _dragProbe = null;
        _dragProbe = probe;
        LogService.Instance.Info("UI", "已打开空白拖拽测试窗口。");
        probe.Activate();
    }

    private static Window? _dragProbe;
}

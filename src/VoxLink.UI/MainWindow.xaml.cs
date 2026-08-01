using System.ComponentModel;
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
        RootLayout.Loaded += RootLayout_Loaded;
        App.Controller.PropertyChanged += Controller_PropertyChanged;
        App.Controller.OnboardingRequested += Controller_OnboardingRequested;
        EnsureSettingsSubscribed();
        LogService.Instance.Info(
            "UI",
            $"窗口已创建：ExtendsContentIntoTitleBar={ExtendsContentIntoTitleBar}，最小尺寸 {AppWindow.Size.Width}x{AppWindow.Size.Height}。");
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
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VoxLink.UI.Controls;
using VoxLink.UI.Core.Models;
using VoxLink.UI.Core.Services;
using VoxLink.UI.Infrastructure;
using VoxLink.UI.Pages;
using Windows.Graphics;


namespace VoxLink.UI;

public sealed partial class MainWindow : Window
{
    private const uint WmSetIcon = 0x0080;
    private const nint IconBig = 1;
    private const nint IconSmall = 0;
    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x0010;
    private const uint DefaultSize = 0x0040;
    private const int SmCxsmicon = 49;
    private const int SmCysmicon = 50;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(
        IntPtr hInstance,
        string lpszName,
        uint type,
        int cx,
        int cy,
        uint fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private enum CloseChoice
    {
        Exit,
        KeepBackground,
        Cancel,
        Retry
    }

    private bool _allowClose;
    private bool _closeRequested;
    private bool _onboardingOpen;
    private bool _onboardingPending;
    private AppSettings? _subscribedSettings;
    private MicaBackdrop? _micaBackdrop;
    private TrayIconService? _trayIcon;
    private bool _hiddenToTray;

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
        _trayIcon = new TrayIconService(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        _trayIcon.RestoreRequested += TrayIcon_RestoreRequested;
        _trayIcon.MenuProvider = BuildTrayMenu;
        EnsureTrayIconVisibility();
        RootLayout.Loaded += RootLayout_Loaded;
        App.Controller.PropertyChanged += Controller_PropertyChanged;
        App.Controller.OnboardingRequested += Controller_OnboardingRequested;
        App.Controller.ConversationHistoryRequested += Controller_ConversationHistoryRequested;
        App.Controller.LocalModelsRequested += Controller_LocalModelsRequested;
        EnsureSettingsSubscribed();
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

    /// <summary>应用窗口外观：Mica 透明背景 与 自绘标题栏（外观偏好，即时生效）。</summary>
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

        ExtendsContentIntoTitleBar = true;
        AppTitleBar.Visibility = Visibility.Visible;
        NavView.IsPaneToggleButtonVisible = false;
        SetTitleBar(AppTitleBar);
    }

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppSettings.UseMicaBackdrop))
        {
            ApplyWindowChrome();
            LogService.Instance.Info(
                "UI",
                $"窗口外观已更新：Mica={App.Controller.Settings.UseMicaBackdrop}。");
        }

        if (args.PropertyName == nameof(AppSettings.MinimizeToTray))
        {
            EnsureTrayIconVisibility();
            LogService.Instance.Info(
                "UI",
                $"最小化到托盘已更新：{App.Controller.Settings.MinimizeToTray}。");
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
            "history" => typeof(ConversationHistoryPage),
            "audio" => typeof(AudioPage),
            "vrchat" => typeof(VRChatPage),
            "overlay" => typeof(OverlayPage),
            "speech" => typeof(SpeechPage),
            "models" => typeof(ModelProvidersPage),
            "local-models" => typeof(LocalModelsPage),
            "advanced" => typeof(AdvancedPage),
            "logs" => typeof(LogsPage),
            "about" => typeof(AboutPage),
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

    private void Controller_ConversationHistoryRequested(object? sender, EventArgs args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            NavView.SelectedItem = null;
            ContentFrame.Navigate(typeof(ConversationHistoryPage));
        });

    private void Controller_LocalModelsRequested(object? sender, EventArgs args) =>
        DispatcherQueue.TryEnqueue(() => NavView.SelectedItem = LocalModelsNavigationItem);
    private async void RootLayout_Loaded(object sender, RoutedEventArgs args)
    {
        ApplyWindowIcon();
        await TryShowOnboardingAsync();
    }

    /// <summary>
    /// 窗口显示后再次强制应用图标：WinUI 3 的 AppWindow.SetIcon 在窗口激活前调用
    /// 可能不生效，导致任务栏仍显示旧图标；这里通过 WM_SETICON 直接写入窗口句柄。
    /// </summary>
    private void ApplyWindowIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            AppWindow.SetIcon(iconPath);

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var big = LoadImageW(IntPtr.Zero, iconPath, ImageIcon, 0, 0, LoadFromFile | DefaultSize);
            var small = LoadImageW(
                IntPtr.Zero,
                iconPath,
                ImageIcon,
                GetSystemMetrics(SmCxsmicon),
                GetSystemMetrics(SmCysmicon),
                LoadFromFile);
            // WM_SETICON 后窗口仍持有句柄用于绘制（与 WPF/WinForms 行为一致），
            // 句柄随窗口生命周期存活，进程退出时由系统统一回收，因此不主动释放。
            if (big != IntPtr.Zero)
            {
                SendMessageW(hwnd, WmSetIcon, IconBig, big);
            }

            if (small != IntPtr.Zero)
            {
                SendMessageW(hwnd, WmSetIcon, IconSmall, small);
            }
        }
        catch
        {
            // 图标应用失败不影响窗口启动，托盘与资源管理器图标仍由 exe 提供。
        }
    }


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

        _onboardingOpen = true;
        try
        {
            var dialog = new OnboardingDialog(App.Controller)
            {
                XamlRoot = RootLayout.XamlRoot
            };
            _onboardingPending = false;
            await dialog.ShowAsync();
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            _onboardingPending = true;
            _ = RetryOnboardingAsync();
        }
        finally
        {
            _onboardingOpen = false;
        }
    }

    private async Task RetryOnboardingAsync()
    {
        await Task.Delay(250);
        if (!_closeRequested && _onboardingPending)
        {
            DispatcherQueue.TryEnqueue(async () => await TryShowOnboardingAsync());
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
        await TryExitAsync();
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPresenterChange
            || !App.Controller.Settings.MinimizeToTray
            || sender.Presenter is not OverlappedPresenter presenter
            || presenter.State != OverlappedPresenterState.Minimized)
        {
            return;
        }

        _hiddenToTray = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (App.Controller.Settings.MinimizeToTray && _hiddenToTray)
            {
                sender.Hide();
            }
        });
    }

    private void EnsureTrayIconVisibility()
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.Visible = App.Controller.Settings.MinimizeToTray;
        if (!App.Controller.Settings.MinimizeToTray && _hiddenToTray)
        {
            RestoreFromTray();
        }
    }

    private void RestoreFromTray()
    {
        _hiddenToTray = false;
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }

        Activate();
    }

    private void TrayIcon_RestoreRequested() =>
        DispatcherQueue.TryEnqueue(RestoreFromTray);

    private IReadOnlyList<TrayIconService.TrayMenuItem> BuildTrayMenu()
    {
        var settings = App.Controller.Settings;
        var canSwitch = !App.Controller.IsBusy && !App.Controller.HasBusyLocalModels;
        var models = App.Controller.LocalModels;
        bool Installed(string modelId) => models.Any(model =>
            model.Id.Equals(modelId, StringComparison.Ordinal) && model.Installed);

        void CommitSettingsChange()
        {
            App.Controller.NotifySettingsChanged();
            App.Controller.MarkSessionRestartRequired();
        }

        void SelectTranslation(TranslationBackend backend)
        {
            settings.SelectTranslationBackend(backend);
            CommitSettingsChange();
        }

        void SelectAsr(AsrProvider provider)
        {
            settings.SelectAsrProvider(provider);
            CommitSettingsChange();
        }

        void SelectSpeech(SpeechServiceMode mode)
        {
            settings.SelectSpeechService(mode);
            CommitSettingsChange();
        }

        void ActivateLocal(string modelId) =>
            _ = App.Controller.InstallAndActivateLocalModelAsync(modelId);

        var translationItems = new List<TrayIconService.TrayMenuItem>
        {
            new(
                "公共免密",
                () => SelectTranslation(TranslationBackend.PublicFree),
                Checked: !settings.UseAiTranslation
                    || settings.TranslationBackend == TranslationBackend.PublicFree),
            new(
                "本地 MiniCPM5-1B",
                () => ActivateLocal(LocalModelIds.MiniCpm51BGguf),
                Checked: settings.UseAiTranslation
                    && settings.TranslationBackend == TranslationBackend.LocalMiniCpm,
                Enabled: canSwitch && Installed(LocalModelIds.MiniCpm51BGguf)),
            new(
                "本地混元翻译 HY-MT1.5-1.8B（GGUF）",
                () => ActivateLocal(LocalModelIds.HyMt15Gguf),
                Checked: settings.UseAiTranslation
                    && settings.TranslationBackend == TranslationBackend.LocalHyMtGguf,
                Enabled: canSwitch && Installed(LocalModelIds.HyMt15Gguf)),
            new(
                "DeepSeek",
                () => SelectTranslation(TranslationBackend.DeepSeek),
                Checked: settings.UseAiTranslation
                    && settings.TranslationBackend == TranslationBackend.DeepSeek),
            new(
                "OpenAI 兼容",
                () => SelectTranslation(TranslationBackend.OpenAiCompatible),
                Checked: settings.UseAiTranslation
                    && settings.TranslationBackend == TranslationBackend.OpenAiCompatible),
            new(
                "自定义服务",
                () => SelectTranslation(TranslationBackend.Custom),
                Checked: settings.UseAiTranslation
                    && settings.TranslationBackend == TranslationBackend.Custom)
        };

        var asrItems = new List<TrayIconService.TrayMenuItem>
        {
            new(
                "Whisper tiny",
                () => ActivateLocal(LocalModelIds.WhisperTiny),
                Checked: !settings.UseCloudAsr
                    && settings.AsrProtocol != AsrProtocol.LocalSenseVoice
                    && settings.WhisperModel == "tiny",
                Enabled: canSwitch && Installed(LocalModelIds.WhisperTiny)),
            new(
                "Whisper base",
                () => ActivateLocal(LocalModelIds.WhisperBase),
                Checked: !settings.UseCloudAsr
                    && settings.AsrProtocol != AsrProtocol.LocalSenseVoice
                    && settings.WhisperModel == "base",
                Enabled: canSwitch && Installed(LocalModelIds.WhisperBase)),
            new(
                "Whisper small",
                () => ActivateLocal(LocalModelIds.WhisperSmall),
                Checked: !settings.UseCloudAsr
                    && settings.AsrProtocol != AsrProtocol.LocalSenseVoice
                    && settings.WhisperModel == "small",
                Enabled: canSwitch && Installed(LocalModelIds.WhisperSmall)),
            new(
                "Whisper large-v3-turbo",
                () => ActivateLocal(LocalModelIds.WhisperLargeV3Turbo),
                Checked: !settings.UseCloudAsr
                    && settings.AsrProtocol != AsrProtocol.LocalSenseVoice
                    && settings.WhisperModel == "large-v3-turbo",
                Enabled: canSwitch && Installed(LocalModelIds.WhisperLargeV3Turbo)),
            new(
                "本地 SenseVoice",
                () => ActivateLocal(LocalModelIds.SenseVoiceSmall),
                Checked: !settings.UseCloudAsr && settings.AsrProtocol == AsrProtocol.LocalSenseVoice,
                Enabled: canSwitch && Installed(LocalModelIds.SenseVoiceSmall)),
            new(
                "Soniox",
                () => SelectAsr(AsrProvider.Soniox),
                Checked: settings.UseCloudAsr && settings.AsrProvider == AsrProvider.Soniox),
            new(
                "硅基流动",
                () => SelectAsr(AsrProvider.SiliconFlow),
                Checked: settings.UseCloudAsr
                    && settings.AsrProvider == AsrProvider.SiliconFlow),
            new(
                "小米 MiMo",
                () => SelectAsr(AsrProvider.MiMo),
                Checked: settings.UseCloudAsr && settings.AsrProvider == AsrProvider.MiMo),
            new(
                "OpenAI 兼容",
                () => SelectAsr(AsrProvider.OpenAiCompatible),
                Checked: settings.UseCloudAsr
                    && settings.AsrProvider == AsrProvider.OpenAiCompatible),
            new(
                "自定义服务",
                () => SelectAsr(AsrProvider.Custom),
                Checked: settings.UseCloudAsr && settings.AsrProvider == AsrProvider.Custom)
        };

        var speechItems = new List<TrayIconService.TrayMenuItem>
        {
            new(
                "系统语音",
                () => SelectSpeech(SpeechServiceMode.SystemFallback),
                Checked: settings.SpeechServiceMode == SpeechServiceMode.SystemFallback),
            new(
                "本地 Kokoro-82M",
                () => ActivateLocal(LocalModelIds.Kokoro82M),
                Checked: settings.SpeechServiceMode == SpeechServiceMode.Kokoro,
                Enabled: canSwitch && Installed(LocalModelIds.Kokoro82M)),
            new(
                "远程语音服务",
                () => SelectSpeech(SpeechServiceMode.Remote),
                Checked: settings.SpeechServiceMode == SpeechServiceMode.Remote)
        };

        return
        [
            new("打开 VoxLink", () => DispatcherQueue.TryEnqueue(RestoreFromTray)),
            new(
                App.Controller.IsRunning ? "停止翻译" : "开始翻译",
                () => DispatcherQueue.TryEnqueue(() => _ = App.Controller.ToggleSessionAsync())),
            new(IsSeparator: true),
            new("运行模式", Children:
            [
                new(
                    "文字模式（Chatbox）",
                    () =>
                    {
                        settings.SpeakMyTranslation = false;
                        CommitSettingsChange();
                    },
                    Checked: !settings.SpeakMyTranslation),
                new(
                    "VRChat 语音模式",
                    () =>
                    {
                        settings.SpeakMyTranslation = true;
                        CommitSettingsChange();
                    },
                    Checked: settings.SpeakMyTranslation)
            ]),
            new("翻译服务", Children: translationItems),
            new("语音识别", Children: asrItems),
            new("语音合成", Children: speechItems),
            new(IsSeparator: true),
            new("退出 VoxLink", () => DispatcherQueue.TryEnqueue(() => _ = TryExitAsync()))
        ];
    }

    private async Task TryExitAsync()
    {
        if (_allowClose || _closeRequested)
        {
            return;
        }

        _closeRequested = true;
        try
        {
            var choice = await ConfirmCloseAsync();
            while (choice == CloseChoice.Retry && !_allowClose)
            {
                await Task.Delay(250);
                choice = await ConfirmCloseAsync();
            }
            if (choice == CloseChoice.Cancel)
            {
                return;
            }

            if (choice == CloseChoice.KeepBackground)
            {
                KeepRunningInBackground();
                return;
            }

            LogService.Instance.Info("UI", "VoxLink 即将退出。");
            await App.Controller.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
            _allowClose = true;
            Cleanup();
            Close();
        }
        catch (Exception exception)
        {
            LogService.Instance.Error("UI", exception, "关闭流程异常");
            System.Diagnostics.Debug.WriteLine($"VoxLink shutdown failed: {exception}");
        }
        finally
        {
            if (!_allowClose)
            {
                _closeRequested = false;
            }
        }
    }

    private void Cleanup()
    {
        AppWindow.Closing -= AppWindow_Closing;
        AppWindow.Changed -= AppWindow_Changed;
        RootLayout.Loaded -= RootLayout_Loaded;
        App.Controller.PropertyChanged -= Controller_PropertyChanged;
        App.Controller.OnboardingRequested -= Controller_OnboardingRequested;
        App.Controller.ConversationHistoryRequested -= Controller_ConversationHistoryRequested;
        App.Controller.LocalModelsRequested -= Controller_LocalModelsRequested;
        if (_trayIcon is not null)
        {
            _trayIcon.RestoreRequested -= TrayIcon_RestoreRequested;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }

    private async Task<CloseChoice> ConfirmCloseAsync()
    {
        if (!App.Controller.Settings.ConfirmOnClose || RootLayout.XamlRoot is null)
        {
            return CloseChoice.Exit;
        }

        var dialog = new ContentDialog
        {
            Title = "关闭 VoxLink？",
            Content = "「退出并停止」会停止翻译并关闭音频引擎；「隐藏到托盘」会继续在后台翻译，可从托盘图标恢复。",
            PrimaryButtonText = "退出并停止",
            SecondaryButtonText = "隐藏到托盘",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootLayout.XamlRoot
        };

        try
        {
            return await dialog.ShowAsync() switch
            {
                ContentDialogResult.Primary => CloseChoice.Exit,
                ContentDialogResult.Secondary => CloseChoice.KeepBackground,
                _ => CloseChoice.Cancel
            };
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine("退出确认正在等待当前设置对话框关闭。");
            System.Diagnostics.Debug.WriteLine(exception);
            return CloseChoice.Retry;
        }
        catch (ObjectDisposedException exception)
        {
            LogService.Instance.Warning("UI", $"退出确认对话框无法显示，已取消退出：{exception}");
            return CloseChoice.Cancel;
        }
    }

    private void KeepRunningInBackground()
    {
        App.Controller.Settings.MinimizeToTray = true;
        EnsureTrayIconVisibility();
        _hiddenToTray = true;
        AppWindow.Hide();
    }
}

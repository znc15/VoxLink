using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoxLink.UI.Controls;
using VoxLink.UI.Pages;
using Windows.Graphics;

namespace VoxLink.UI;

public sealed partial class MainWindow : Window
{
    private bool _allowClose;
    private bool _closeRequested;
    private bool _onboardingOpen;
    private bool _onboardingPending;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
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
        ContentFrame.Navigate(typeof(LivePage));
        UpdateEngineStatus();
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
            System.Diagnostics.Debug.WriteLine($"VoxLink shutdown failed: {exception}");
        }
        finally
        {
            _allowClose = true;
            RootLayout.Loaded -= RootLayout_Loaded;
            App.Controller.PropertyChanged -= Controller_PropertyChanged;
            App.Controller.OnboardingRequested -= Controller_OnboardingRequested;
            Close();
        }
    }
}

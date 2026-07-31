using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VoxLink.UI.Core.Models;
using VoxLink.UI.Core.ViewModels;
using Windows.System;

namespace VoxLink.UI.Pages;

public sealed partial class LivePage : Page
{
    public LivePage()
    {
        InitializeComponent();
        Loaded += LivePage_Loaded;
        Unloaded += LivePage_Unloaded;
    }

    public AppController Controller => App.Controller;

    private void LivePage_Loaded(object sender, RoutedEventArgs args)
    {
        Controller.PropertyChanged += Controller_PropertyChanged;
        Controller.Messages.CollectionChanged += Messages_CollectionChanged;
        RefreshState();
    }

    private void LivePage_Unloaded(object sender, RoutedEventArgs args)
    {
        Controller.PropertyChanged -= Controller_PropertyChanged;
        Controller.Messages.CollectionChanged -= Messages_CollectionChanged;
    }

    private void Controller_PropertyChanged(object? sender, PropertyChangedEventArgs args) => RefreshState();
    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => RefreshMessages();

    private void RefreshState()
    {
        EngineBadgeText.Text = Controller.EngineConnected ? "引擎在线" : "引擎连接中";
        SessionStatusText.Text = Controller.StatusMessage;
        SessionButtonText.Text = Controller.IsRunning ? "停止会话" : "开始会话";
        SessionButtonIcon.Glyph = Controller.IsRunning ? "\uE71A" : "\uE768";
        SessionButton.IsEnabled = !Controller.IsBusy && Controller.EngineConnected;
        SubmitButton.IsEnabled = !Controller.IsBusy && Controller.EngineConnected;
        OscModeButton.IsEnabled = !Controller.IsRunning;
        VoiceModeButton.IsEnabled = !Controller.IsRunning;
        OscModeButton.IsChecked = !Controller.IsVoiceMode;
        VoiceModeButton.IsChecked = Controller.IsVoiceMode;
        var routeStatus = Controller.VoiceRouteStatus;
        ModeStatusText.Text = Controller.Settings.CaptureSystemAudio
            ? routeStatus + " 系统音频翻译已开启。"
            : routeStatus;
        ModelStatusText.Text = Controller.ModelStatus.Length == 0
            ? string.Empty
            : $"{Controller.ModelStatus} {Controller.ModelProgress:P0}";
        ModelStatusText.Visibility = Controller.ModelStatus.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        SessionStatusDot.Fill = new SolidColorBrush(Controller.IsRunning
            ? ColorHelper.FromArgb(255, 15, 123, 63)
            : ColorHelper.FromArgb(255, 96, 105, 114));
        ErrorInfoBar.Message = Controller.ErrorMessage ?? string.Empty;
        ErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(Controller.ErrorMessage);
        UpdateInfoBar.Message = Controller.UpdateStatusText ?? "发现新版本。";
        UpdateInfoBar.IsOpen = Controller.UpdateBannerVisible;
        RestartHintBar.IsOpen = Controller.NeedsSessionRestart;
        RefreshMessages();
    }

    private void RefreshMessages()
    {
        MessageCountText.Text = Controller.Messages.Count.ToString();
        EmptyState.Visibility = Controller.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConversationList.Visibility = Controller.Messages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (Controller.Messages.Count > 0)
        {
            ConversationList.ScrollIntoView(Controller.Messages[^1]);
        }
    }

    private void ErrorInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => Controller.DismissError();
    private void UpdateInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => Controller.DismissUpdateBanner();
    private void OpenRelease_Click(object sender, RoutedEventArgs args) => Controller.OpenLatestReleasePage();
    private void Language_SelectionChanged(object sender, SelectionChangedEventArgs args) => Controller.NotifySettingsChanged();
    private void SwapButton_Click(object sender, RoutedEventArgs args) => Controller.SwapLanguages();

    private void OscModeButton_Click(object sender, RoutedEventArgs args) =>
        Controller.ApplyQuickStartMode(QuickStartMode.OscText);

    private void VoiceModeButton_Click(object sender, RoutedEventArgs args) =>
        Controller.ApplyQuickStartMode(QuickStartMode.VrChatVoice);

    private void Onboarding_Click(object sender, RoutedEventArgs args) => Controller.RequestOnboarding();
    private async void SessionButton_Click(object sender, RoutedEventArgs args) => await Controller.ToggleSessionAsync();
    private void ClearMessages_Click(object sender, RoutedEventArgs args) => Controller.ClearMessages();

    private async void SpeakMessage_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: ConversationMessage message })
        {
            await Controller.SpeakAsync(message);
        }
    }

    private void TranslateModeButton_Click(object sender, RoutedEventArgs args)
    {
        Controller.ComposerMode = ComposerMode.Translate;
        TranslateModeButton.IsChecked = true;
        GenerateModeButton.IsChecked = false;
        SubmitButtonText.Text = "翻译并发送";
    }

    private void GenerateModeButton_Click(object sender, RoutedEventArgs args)
    {
        if (!Controller.CanGenerate)
        {
            GenerateModeButton.IsChecked = false;
            TranslateModeButton.IsChecked = true;
            Controller.ComposerMode = ComposerMode.Translate;
            return;
        }

        Controller.ComposerMode = ComposerMode.Generate;
        TranslateModeButton.IsChecked = false;
        GenerateModeButton.IsChecked = true;
        SubmitButtonText.Text = "生成并发送";
    }

    private async void SubmitButton_Click(object sender, RoutedEventArgs args) => await SubmitAsync();

    private async void ComposerBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        var controlPressed = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (args.Key == VirtualKey.Enter && controlPressed)
        {
            args.Handled = true;
            await SubmitAsync();
        }
    }

    private async Task SubmitAsync()
    {
        var text = ComposerBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await Controller.SubmitAsync(text);
        if (Controller.ErrorMessage is null)
        {
            ComposerBox.Text = string.Empty;
        }
    }
}

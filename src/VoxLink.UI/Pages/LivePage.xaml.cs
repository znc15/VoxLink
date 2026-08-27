using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VoxLink.UI.Core.Models;
using VoxLink.UI.Core.ViewModels;
using Windows.System;

namespace VoxLink.UI.Pages;

public sealed partial class LivePage : Page
{
    private bool? _isNarrowLanguageLayout;
    private bool _loadingLanguage;

    public LivePage()
    {
        InitializeComponent();
        Loaded += LivePage_Loaded;
        Unloaded += LivePage_Unloaded;
    }

    public AppController Controller => App.Controller;

    private void LivePage_Loaded(object sender, RoutedEventArgs args)
    {
        VoxLink.UI.Infrastructure.ComboBoxPopupPlacer.Apply(this);
        Controller.PropertyChanged += Controller_PropertyChanged;
        Controller.Messages.CollectionChanged += Messages_CollectionChanged;
        ApplyLanguageSelections();
        RefreshState();
    }

    private void LivePage_Unloaded(object sender, RoutedEventArgs args)
    {
        Controller.PropertyChanged -= Controller_PropertyChanged;
        Controller.Messages.CollectionChanged -= Messages_CollectionChanged;
    }

    private void Controller_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        // 启动时页面先于设置加载完成：Settings 替换后显式回填语言选择，
        // 确保第二目标语言等保存在 settings.json 的选项在重启后仍被恢复。
        if (args.PropertyName == nameof(AppController.Settings))
        {
            ApplyLanguageSelections();
        }

        RefreshState();
    }
    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => RefreshMessages();

    private void RefreshState()
    {
        SessionStatusText.Text = Controller.StatusMessage;
        SessionButtonText.Text = Controller.IsRunning ? "停止翻译" : "开启翻译";
        SessionButtonIcon.Glyph = Controller.IsRunning ? "\uE71A" : "\uE768";
        SessionButton.IsEnabled = !Controller.IsBusy && Controller.EngineConnected;
        SubmitButton.IsEnabled = !Controller.IsBusy && Controller.EngineConnected;
        SubmitButtonText.Text = "翻译并发送";
        ModelStatusText.Text = Controller.ModelStatus.Length == 0
            ? string.Empty
            : $"{Controller.ModelStatus} {Controller.ModelProgress:P0}";
        ModelStatusText.Visibility = Controller.ModelStatus.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        SessionRunningDot.Visibility = Controller.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        SessionStoppedDot.Visibility = Controller.IsRunning ? Visibility.Collapsed : Visibility.Visible;
        var hasError = !string.IsNullOrWhiteSpace(Controller.ErrorMessage);
        ErrorInfoBar.Message = Controller.ErrorMessage ?? string.Empty;
        ErrorInfoBar.IsOpen = hasError;
        ErrorInfoBar.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        var hasWarning = !string.IsNullOrWhiteSpace(Controller.WarningMessage);
        WarningInfoBar.Message = Controller.WarningMessage ?? string.Empty;
        WarningInfoBar.IsOpen = hasWarning;
        WarningInfoBar.Visibility = hasWarning ? Visibility.Visible : Visibility.Collapsed;
        UpdateInfoBar.Message = Controller.UpdateStatusText ?? "发现新版本。";
        UpdateInfoBar.IsOpen = Controller.UpdateBannerVisible;
        UpdateInfoBar.Visibility = Controller.UpdateBannerVisible ? Visibility.Visible : Visibility.Collapsed;
        RestartHintBar.IsOpen = Controller.NeedsSessionRestart;
        RestartHintBar.Visibility = Controller.NeedsSessionRestart ? Visibility.Visible : Visibility.Collapsed;
        RefreshMessages();
    }


    private void RefreshMessages()
    {
        MessageCountText.Text = Controller.Messages.Count.ToString();
        var latest = Controller.Messages.Count > 0 ? Controller.Messages[^1] : null;
        EmptyState.Visibility = latest is null ? Visibility.Visible : Visibility.Collapsed;
        LatestMessagePanel.Visibility = latest is null ? Visibility.Collapsed : Visibility.Visible;
        if (latest is not null)
        {
            LatestDirectionGlyph.Glyph = latest.DirectionGlyph;
            LatestHeaderText.Text = latest.HeaderLabel;
            LatestTimeText.Text = latest.TimeLabel;
            LatestPrimaryText.Text = latest.PrimaryDisplayText;
            LatestSecondaryText.Text = latest.SecondaryDisplayText;
            LatestSourceText.Text = latest.SourceDisplayText;
            LatestSpeakButton.IsEnabled = latest.CanSpeak;
            LatestSpeakButton.Tag = latest;
        }
    }

    private void LanguageGrid_SizeChanged(object sender, SizeChangedEventArgs args) =>
        ApplyLanguageLayout(args.NewSize.Width);

    private void ApplyLanguageLayout(double width)
    {
        if (width <= 0)
        {
            return;
        }

        var isNarrow = width < 720;
        if (_isNarrowLanguageLayout == isNarrow)
        {
            return;
        }

        _isNarrowLanguageLayout = isNarrow;
        if (isNarrow)
        {
            FirstLanguageColumn.Width = new GridLength(1, GridUnitType.Star);
            SwapColumn.Width = new GridLength(0);
            SecondLanguageColumn.Width = new GridLength(0);
            Grid.SetRow(MyLanguageBox, 0);
            Grid.SetColumn(MyLanguageBox, 0);
            Grid.SetRow(SwapButton, 1);
            Grid.SetColumn(SwapButton, 0);
            Grid.SetRow(OtherLanguageBox, 2);
            Grid.SetColumn(OtherLanguageBox, 0);
            Grid.SetRow(SecondaryLanguageBox, 3);
            Grid.SetColumn(SecondaryLanguageBox, 0);
            return;
        }

        FirstLanguageColumn.Width = new GridLength(1, GridUnitType.Star);
        SwapColumn.Width = new GridLength(44);
        SecondLanguageColumn.Width = new GridLength(1, GridUnitType.Star);
        Grid.SetRow(MyLanguageBox, 0);
        Grid.SetColumn(MyLanguageBox, 0);
        Grid.SetRow(SwapButton, 0);
        Grid.SetColumn(SwapButton, 1);
        Grid.SetRow(OtherLanguageBox, 0);
        Grid.SetColumn(OtherLanguageBox, 2);
        Grid.SetRow(SecondaryLanguageBox, 1);
        Grid.SetColumn(SecondaryLanguageBox, 2);
    }

    /// <summary>把已保存的语言设置显式写回下拉框，避免初始化时序导致选择被清空。</summary>
    private void ApplyLanguageSelections()
    {
        _loadingLanguage = true;
        try
        {
            MyLanguageBox.SelectedValue = Controller.Settings.MyLanguageCode;
            OtherLanguageBox.SelectedValue = Controller.Settings.OtherLanguageCode;
            var secondary = Controller.Settings.SecondaryTargetLanguageCode;
            SecondaryLanguageBox.SelectedValue = string.IsNullOrEmpty(secondary) ? string.Empty : secondary;
        }
        finally
        {
            _loadingLanguage = false;
        }
    }

    private void ErrorInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        ErrorInfoBar.Visibility = Visibility.Collapsed;
        Controller.DismissError();
    }

    private void WarningInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        WarningInfoBar.Visibility = Visibility.Collapsed;
        Controller.DismissWarning();
    }
    private void UpdateInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        UpdateInfoBar.Visibility = Visibility.Collapsed;
        Controller.DismissUpdateBanner();
    }

    private void OpenRelease_Click(object sender, RoutedEventArgs args) => Controller.OpenLatestReleasePage();
    private void Language_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loadingLanguage)
        {
            return;
        }

        Controller.NotifySettingsChanged();
    }
    private void SwapButton_Click(object sender, RoutedEventArgs args) => Controller.SwapLanguages();

    private async void SessionButton_Click(object sender, RoutedEventArgs args) => await Controller.ToggleSessionAsync();
    private void ClearMessages_Click(object sender, RoutedEventArgs args) => Controller.ClearMessages();
    private void ViewHistory_Click(object sender, RoutedEventArgs args) => Controller.RequestConversationHistory();

    private async void SpeakMessage_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: ConversationMessage message })
        {
            await Controller.SpeakAsync(message);
        }
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

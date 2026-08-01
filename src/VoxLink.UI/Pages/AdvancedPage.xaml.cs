using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoxLink.UI.Core.Models;
using VoxLink.UI.Core.ViewModels;

namespace VoxLink.UI.Pages;

public sealed partial class AdvancedPage : Page
{
    private bool _loading = true;
    public AdvancedPage()
    {
        InitializeComponent();
        Loaded += AdvancedPage_Loaded;
        Unloaded += AdvancedPage_Unloaded;
    }

    public AppController Controller => App.Controller;

    private void AdvancedPage_Loaded(object sender, RoutedEventArgs args)
    {
        LoadSettingsIntoControls();
        Controller.PropertyChanged += Controller_PropertyChanged;
        RefreshState();
    }

    private void AdvancedPage_Unloaded(object sender, RoutedEventArgs args) =>
        Controller.PropertyChanged -= Controller_PropertyChanged;

    private void Controller_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppController.Settings))
        {
            LoadSettingsIntoControls();
        }

        RefreshState();
    }

    private void LoadSettingsIntoControls()
    {
        _loading = true;
        try
        {
            Bindings.Update();
            SpeakerModeBox.SelectedIndex = (int)Controller.Settings.SpeakerLabelMode;
            SpeechContentButtons.SelectedIndex = Controller.Settings.OutboundSpeechContent == OutboundSpeechContent.Original
                ? 1
                : 0;
        }
        finally
        {
            _loading = false;
        }
    }
    private void RefreshState()
    {
        SpeakerModeDescription.Text = Controller.Settings.SpeakerLabelMode switch
        {
            SpeakerLabelMode.Local when Controller.Settings.UsesStreamingAsr =>
                "流式 ASR 无法可靠对齐本地音频窗口，本次会话会关闭本地标签。",
            SpeakerLabelMode.Local =>
                "对系统回环中的完整 VAD 语句提取本地嵌入，匿名标记为说话人 A/B/C。",
            SpeakerLabelMode.Cloud when !Controller.Settings.SupportsCloudSpeakerLabels =>
                "当前 ASR 不支持云端 speaker ID，本次会话会关闭标签。",
            SpeakerLabelMode.Cloud => "使用 Soniox 返回的 provider speaker ID。",
            _ => "关闭时不区分系统音频中的说话人。"
        };
        LocalSpeakerInfo.IsOpen = Controller.Settings.SpeakerLabelMode == SpeakerLabelMode.Local;
        var wasLoading = _loading;
        _loading = true;
        try
        {
            SpeakMyTranslationSwitch.IsOn = Controller.IsVoiceMode;
        }
        finally
        {
            _loading = wasLoading;
        }
        SpeechContentButtons.IsEnabled = !Controller.IsRunning;
        SpeakMyTranslationSwitch.IsEnabled = !Controller.IsRunning;
        EngineStateText.Text = Controller.EngineConnected ? "已连接" : "未连接";
        SessionStateText.Text = Controller.IsRunning ? "运行中" : "已停止";
        ActivityText.Text = Controller.Activity switch
        {
            "listening" => "正在监听",
            "transcribing" => "正在识别",
            "translating" => "正在翻译",
            "speaking" => "正在播放",
            "error" => "异常",
            "preparing" => "准备中",
            _ => "空闲"
        };
        VersionText.Text = $"VoxLink {Controller.AppVersion.ToString(3)}";
        UpdateStatusText.Text = Controller.UpdateStatusText ?? string.Empty;
        CheckUpdatesButton.IsEnabled = !Controller.IsCheckingForUpdates;
        OpenReleaseButton.Visibility = Controller.IsUpdateAvailable
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SpeakerModeBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || SpeakerModeBox.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse<SpeakerLabelMode>(tag, out var mode))
        {
            return;
        }

        Controller.Settings.SpeakerLabelMode = mode;
        Controller.NotifySettingsChanged();
        RefreshState();
    }

    private void SpeakMyTranslationSwitch_Toggled(object sender, RoutedEventArgs args)
    {
        if (_loading)
        {
            return;
        }

        Controller.ApplyQuickStartMode(SpeakMyTranslationSwitch.IsOn
            ? QuickStartMode.VrChatVoice
            : QuickStartMode.OscText);
        LoadSettingsIntoControls();
        RefreshState();
    }

    private void SpeechContentButtons_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || SpeechContentButtons.SelectedItem is not RadioButton { Tag: string tag }
            || !Enum.TryParse<OutboundSpeechContent>(tag, out var content))
        {
            return;
        }

        Controller.Settings.OutboundSpeechContent = content;
        Controller.NotifySettingsChanged();
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs args) =>
        await Controller.CheckForUpdatesAsync();

    private void OpenRelease_Click(object sender, RoutedEventArgs args) =>
        Controller.OpenLatestReleasePage();

    private void OpenDragProbe_Click(object sender, RoutedEventArgs args) =>
        MainWindow.OpenDragProbe();
}

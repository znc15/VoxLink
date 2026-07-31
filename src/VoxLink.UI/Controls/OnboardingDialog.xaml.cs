using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoxLink.UI.Core.Models;
using VoxLink.UI.Core.ViewModels;
using Windows.System;

namespace VoxLink.UI.Controls;

public sealed partial class OnboardingDialog : ContentDialog
{
    private readonly AppController _controller;
    private int _step;
    private bool _loading = true;

    public OnboardingDialog(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        LoadSettings();
        _loading = false;
        UpdateStep();
    }

    private void LoadSettings()
    {
        ModeButtons.SelectedIndex = _controller.Settings.QuickStartMode == QuickStartMode.VrChatVoice ? 1 : 0;
        MyLanguageBox.ItemsSource = _controller.Languages;
        OtherLanguageBox.ItemsSource = _controller.Languages;
        MyLanguageBox.SelectedValue = _controller.Settings.MyLanguageCode;
        OtherLanguageBox.SelectedValue = _controller.Settings.OtherLanguageCode;
        ReloadDevices();
        SpeechContentButtons.SelectedIndex = _controller.Settings.OutboundSpeechContent == OutboundSpeechContent.Original
            ? 1
            : 0;
        ApplyModeState();
    }

    private void ReloadDevices()
    {
        MicrophoneBox.ItemsSource = null;
        VoiceOutputBox.ItemsSource = null;
        MicrophoneBox.ItemsSource = _controller.MicrophoneDevices;
        VoiceOutputBox.ItemsSource = _controller.RenderDevices;

        var microphoneId = _controller.Settings.MicrophoneDeviceId;
        if (string.IsNullOrWhiteSpace(microphoneId))
        {
            microphoneId = _controller.MicrophoneDevices.FirstOrDefault(device => device.IsDefault)?.Id;
        }
        MicrophoneBox.SelectedValue = microphoneId;

        var outputId = _controller.Settings.VoiceOutputDeviceId;
        if (string.IsNullOrWhiteSpace(outputId))
        {
            outputId = _controller.FindVirtualCable()?.Id;
        }
        VoiceOutputBox.SelectedValue = outputId;
        UpdateVirtualCableInfo();
    }

    private void ApplyModeState()
    {
        var voiceMode = ModeButtons.SelectedIndex == 1;
        VoiceSettingsPanel.Visibility = voiceMode ? Visibility.Visible : Visibility.Collapsed;
        VoiceVrChatInstructions.Visibility = voiceMode ? Visibility.Visible : Visibility.Collapsed;
        TestVoiceButton.Visibility = voiceMode ? Visibility.Visible : Visibility.Collapsed;
        UpdateVirtualCableInfo();
    }

    private void UpdateVirtualCableInfo()
    {
        if (_controller.HasVirtualCable)
        {
            VirtualCableInfoBar.Severity = InfoBarSeverity.Success;
            VirtualCableInfoBar.Title = "已检测到虚拟声卡";
            VirtualCableInfoBar.Message = $"将自动优先使用 {_controller.VirtualCableName}。";
            return;
        }

        VirtualCableInfoBar.Severity = InfoBarSeverity.Warning;
        VirtualCableInfoBar.Title = "未检测到虚拟声卡";
        VirtualCableInfoBar.Message = "VRChat 不接受 OSC 音频。请安装 VB-CABLE 或 Voicemeeter，刷新设备后选择其播放端。";
    }

    private void UpdateStep()
    {
        ModeStep.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        DevicesStep.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        TestStep.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        StepCounterText.Text = $"{_step + 1} / 3";
        StepTitleText.Text = _step switch
        {
            0 => "选择使用方式",
            1 => "配置语言与设备",
            _ => "连接 VRChat"
        };
        PrimaryButtonText = _step == 2 ? "完成设置" : "下一步";
        SecondaryButtonText = _step == 0 ? string.Empty : "上一步";
        GuideErrorBar.IsOpen = false;
        ApplyModeState();
    }

    private void ModeButtons_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || ModeButtons.SelectedIndex < 0)
        {
            return;
        }

        _controller.ApplyQuickStartMode(ModeButtons.SelectedIndex == 1
            ? QuickStartMode.VrChatVoice
            : QuickStartMode.OscText);
        ReloadDevices();
        ApplyModeState();
    }

    private void SwapLanguages_Click(object sender, RoutedEventArgs args)
    {
        (MyLanguageBox.SelectedValue, OtherLanguageBox.SelectedValue) =
            (OtherLanguageBox.SelectedValue, MyLanguageBox.SelectedValue);
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs args)
    {
        await _controller.RefreshDevicesAsync();
        ReloadDevices();
        ShowControllerResult("设备列表已刷新。", InfoBarSeverity.Success);
    }

    private void VoiceOutputBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || VoiceOutputBox.SelectedValue is not string deviceId)
        {
            return;
        }

        _controller.Settings.VoiceOutputDeviceId = deviceId;
        _controller.NotifySettingsChanged();
        UpdateVirtualCableInfo();
    }

    private async void OpenVirtualCableDownload_Click(object sender, RoutedEventArgs args) =>
        await Launcher.LaunchUriAsync(new Uri("https://vb-audio.com/Cable/"));

    private async void TestOsc_Click(object sender, RoutedEventArgs args) =>
        await RunTestAsync(_controller.TestVrChatOscAsync, "Chatbox 测试消息已发送。请检查 VRChat。", requireVoiceRoute: false);

    private async void TestVoice_Click(object sender, RoutedEventArgs args) =>
        await RunTestAsync(_controller.TestVoiceOutputAsync, "测试语音已发送到虚拟声卡。请查看 VRChat 麦克风电平。", requireVoiceRoute: true);

    private async Task RunTestAsync(Func<Task> test, string successMessage, bool requireVoiceRoute)
    {
        CommitSelections();
        if (requireVoiceRoute && _controller.ValidateVoiceRouteSettings() is { } routeError)
        {
            ShowGuideError(routeError);
            return;
        }

        TestProgress.IsActive = true;
        TestProgress.Visibility = Visibility.Visible;
        try
        {
            await test();
            ShowControllerResult(successMessage, InfoBarSeverity.Success);
        }
        finally
        {
            TestProgress.IsActive = false;
            TestProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowControllerResult(string successMessage, InfoBarSeverity successSeverity)
    {
        if (!string.IsNullOrWhiteSpace(_controller.ErrorMessage))
        {
            ShowGuideError(_controller.ErrorMessage);
            return;
        }

        GuideErrorBar.Severity = successSeverity;
        GuideErrorBar.Message = successMessage;
        GuideErrorBar.IsOpen = true;
        TestStatusText.Text = _controller.StatusMessage;
    }

    private void ShowGuideError(string message)
    {
        GuideErrorBar.Severity = InfoBarSeverity.Warning;
        GuideErrorBar.Message = message;
        GuideErrorBar.IsOpen = true;
        TestStatusText.Text = message;
    }

    private void CommitSelections()
    {
        if (MyLanguageBox.SelectedValue is string myLanguage)
        {
            _controller.Settings.MyLanguageCode = myLanguage;
        }
        if (OtherLanguageBox.SelectedValue is string otherLanguage)
        {
            _controller.Settings.OtherLanguageCode = otherLanguage;
        }
        if (MicrophoneBox.SelectedValue is string microphoneId)
        {
            _controller.Settings.MicrophoneDeviceId = microphoneId;
        }
        if (VoiceOutputBox.SelectedValue is string outputId)
        {
            _controller.Settings.VoiceOutputDeviceId = outputId;
        }

        _controller.Settings.OutboundSpeechContent = SpeechContentButtons.SelectedIndex == 1
            ? OutboundSpeechContent.Original
            : OutboundSpeechContent.Translation;
        _controller.NotifySettingsChanged();
    }

    private async void Dialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_step == 0)
        {
            _step = 1;
            UpdateStep();
            return;
        }

        CommitSelections();
        if (_step == 1)
        {
            if (_controller.ValidateLanguageSettingsForOnboarding() is { } languageError)
            {
                ShowGuideError(languageError);
                return;
            }
            if (_controller.ValidateMicrophoneSettingsForOnboarding() is { } microphoneError)
            {
                ShowGuideError(microphoneError);
                return;
            }
            if (_controller.ValidateVoiceRouteSettings() is { } routeError)
            {
                ShowGuideError(routeError);
                return;
            }
            _step = 2;
            UpdateStep();
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            _controller.CompleteOnboarding();
            await _controller.SaveNowAsync();
            args.Cancel = false;
        }
        catch (Exception exception)
        {
            ShowGuideError(exception.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void Dialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_step == 0)
        {
            return;
        }

        args.Cancel = true;
        _step--;
        UpdateStep();
    }
}

using System.Collections.Specialized;
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
    private bool _testInProgress;

    public OnboardingDialog(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        LoadSettings();
        _loading = false;
        UpdateStep();
        _controller.MicrophoneDevices.CollectionChanged += Devices_CollectionChanged;
        _controller.RenderDevices.CollectionChanged += Devices_CollectionChanged;
        Closed += OnboardingDialog_Closed;
    }

    private void OnboardingDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _controller.MicrophoneDevices.CollectionChanged -= Devices_CollectionChanged;
        _controller.RenderDevices.CollectionChanged -= Devices_CollectionChanged;
    }

    private void LoadSettings()
    {
        MyLanguageBox.ItemsSource = _controller.Languages;
        OtherLanguageBox.ItemsSource = _controller.Languages;
        MyLanguageBox.SelectedValue = _controller.Settings.MyLanguageCode;
        OtherLanguageBox.SelectedValue = _controller.Settings.OtherLanguageCode;
        ReloadDevices();
        SpeechContentButtons.SelectedIndex = _controller.Settings.OutboundSpeechContent == OutboundSpeechContent.Original
            ? 1
            : 0;
        UpdateVirtualCableInfo();
    }

    private void ReloadDevices()
    {
        MicrophoneBox.ItemsSource = null;
        VoiceOutputBox.ItemsSource = null;
        MicrophoneBox.ItemsSource = _controller.MicrophoneDevices;
        VoiceOutputBox.ItemsSource = _controller.RenderDevices;
        ReapplyDeviceSelections();
    }

    private void Devices_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) =>
        ReapplyDeviceSelections();

    private void ReapplyDeviceSelections()
    {
        _loading = true;
        try
        {
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
        finally
        {
            _loading = false;
        }
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
        VirtualCableInfoBar.Message = "朗读我的译文需要虚拟声卡。请安装 VB-CABLE 或 Voicemeeter，刷新设备后选择其播放端。";
    }

    private void UpdateStep()
    {
        DevicesStep.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        TestStep.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        StepCounterText.Text = $"{_step + 1} / 2";
        StepTitleText.Text = _step switch
        {
            0 => "配置语言与设备",
            _ => "测试连接"
        };
        PrimaryButtonText = _step == 1 ? "完成设置" : "下一步";
        SecondaryButtonText = _step == 0 ? string.Empty : "上一步";
        GuideErrorBar.IsOpen = false;
        VoiceVrChatInstructions.Visibility = _controller.Settings.SpeakMyTranslation
            ? Visibility.Visible
            : Visibility.Collapsed;
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
        if (_testInProgress || _controller.IsBusy)
        {
            return;
        }

        CommitSelections();
        if (requireVoiceRoute)
        {
            if (!_controller.Settings.SpeakMyTranslation)
            {
                _controller.Settings.SpeakMyTranslation = true;
            }
            if (_controller.ValidateVoiceRouteSettings() is { } routeError)
            {
                ShowGuideError(routeError);
                return;
            }
        }

        _testInProgress = true;
        TestOscButton.IsEnabled = false;
        TestVoiceButton.IsEnabled = false;
        TestProgress.IsActive = true;
        TestProgress.Visibility = Visibility.Visible;
        try
        {
            await test();
            ShowControllerResult(successMessage, InfoBarSeverity.Success);
        }
        finally
        {
            _testInProgress = false;
            TestOscButton.IsEnabled = true;
            TestVoiceButton.IsEnabled = true;
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
        TestStatusText.Text = _controller.TestResultMessage;
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
            CommitSelections();
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
            _step = 1;
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

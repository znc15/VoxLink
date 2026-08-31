using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using VoxLink.UI.Core.Models;
using VoxLink.UI.Core.ViewModels;

namespace VoxLink.UI.Pages;

public sealed partial class AudioPage : Page
{
    private bool _loading = true;

    public AudioPage()
    {
        InitializeComponent();
        Loaded += AudioPage_Loaded;
        Unloaded += AudioPage_Unloaded;
    }

    public AppController Controller => App.Controller;

    private void AudioPage_Loaded(object sender, RoutedEventArgs args)
    {
        VoxLink.UI.Infrastructure.ComboBoxPopupPlacer.Apply(this);
        Controller.MicrophoneDevices.CollectionChanged += Devices_CollectionChanged;
        Controller.RenderDevices.CollectionChanged += Devices_CollectionChanged;
        LoadSettingsIntoControls();
        Controller.PropertyChanged += Controller_PropertyChanged;
        RefreshState();
    }
    private void AudioPage_Unloaded(object sender, RoutedEventArgs args)
    {
        Controller.MicrophoneDevices.CollectionChanged -= Devices_CollectionChanged;
        Controller.RenderDevices.CollectionChanged -= Devices_CollectionChanged;
        Controller.PropertyChanged -= Controller_PropertyChanged;
    }
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
            ReapplyDeviceSelections();
            VoicePreprocessingModeButtons.SelectedIndex = Controller.Settings.VoicePreprocessingMode switch
            {
                VoicePreprocessingMode.WebRtc => 1,
                VoicePreprocessingMode.RNNoise => 2,
                _ => 0
            };
        }
        finally
        {
            _loading = false;
        }
    }
    private void ReapplyDeviceSelections()
    {
        MicrophoneBox.SelectedValue = Controller.Settings.MicrophoneDeviceId;
        LoopbackBox.SelectedValue = Controller.Settings.SystemAudioDeviceId;
        VoiceOutputBox.SelectedValue = Controller.Settings.VoiceOutputDeviceId;
        VoiceMonitorBox.SelectedValue = string.IsNullOrWhiteSpace(Controller.Settings.VoiceMonitorDeviceId)
            ? Controller.RenderDevices.FirstOrDefault(device => device.IsDefault)?.Id
            : Controller.Settings.VoiceMonitorDeviceId;
    }
    private void Devices_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        _loading = true;
        try
        {
            ReapplyDeviceSelections();
        }
        finally
        {
            _loading = false;
        }
    }
    private void RefreshState()
    {
        ThresholdValueText.Text = Controller.Settings.VoiceThreshold.ToString("0.000");
        SilenceValueText.Text = $"{Controller.Settings.SilenceDurationMs} ms";
        OutputVolumeValueText.Text = Controller.Settings.TtsOutputVolume.ToString("0%");
        AudioErrorBar.Message = Controller.ErrorMessage ?? string.Empty;
        AudioErrorBar.IsOpen = !string.IsNullOrWhiteSpace(Controller.ErrorMessage);
        RestartHintBar.IsOpen = Controller.NeedsSessionRestart;

        var unlocked = !Controller.IsRunning && !Controller.IsBusy;
        OutputVolumeSlider.IsEnabled = unlocked;
        VoiceMonitorToggle.IsEnabled = unlocked;
        VoiceMonitorBox.IsEnabled = unlocked && Controller.Settings.EnableVoiceMonitoring;
        MonitorFeedbackBar.IsOpen = Controller.Settings.EnableVoiceMonitoring
            && Controller.Settings.CaptureSystemAudio;
    }

    private void Device_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading)
        {
            return;
        }

        // 设备列表异步加载时 ComboBox 会把不匹配的选择重置为 null，
        // 这里只写入有效设备 ID，避免把空值回写并持久化导致路由丢失。
        if (ReferenceEquals(sender, MicrophoneBox)
            && MicrophoneBox.SelectedValue is string { Length: > 0 } microphoneId)
        {
            Controller.Settings.MicrophoneDeviceId = microphoneId;
        }
        else if (ReferenceEquals(sender, LoopbackBox)
            && LoopbackBox.SelectedValue is string { Length: > 0 } loopbackId)
        {
            Controller.Settings.SystemAudioDeviceId = loopbackId;
        }
        else if (ReferenceEquals(sender, VoiceOutputBox)
            && VoiceOutputBox.SelectedValue is string { Length: > 0 } voiceId)
        {
            Controller.Settings.VoiceOutputDeviceId = voiceId;
        }
        else
        {
            return;
        }

        Controller.NotifySettingsChanged();
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs args) =>
        await Controller.RefreshDevicesAsync();

    private void VoicePreprocessingMode_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || VoicePreprocessingModeButtons.SelectedIndex < 0)
        {
            return;
        }

        Controller.Settings.VoicePreprocessingMode = VoicePreprocessingModeButtons.SelectedIndex switch
        {
            1 => VoicePreprocessingMode.WebRtc,
            2 => VoicePreprocessingMode.RNNoise,
            _ => VoicePreprocessingMode.Off
        };
    }

    private void ThresholdSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!_loading)
        {
            RefreshState();
        }
    }
    private void SilenceSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!_loading)
        {
            RefreshState();
        }
    }

    private void OutputVolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!_loading)
        {
            Controller.NotifySettingsChanged();
            RefreshState();
        }
    }

    private void VoiceMonitorToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (_loading)
        {
            return;
        }

        Controller.NotifySettingsChanged();
        RefreshState();
    }

    private void VoiceMonitorBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading)
        {
            return;
        }

        if (VoiceMonitorBox.SelectedValue is string { Length: > 0 } monitorId)
        {
            var selected = Controller.RenderDevices.FirstOrDefault(device => device.Id == monitorId);
            Controller.Settings.VoiceMonitorDeviceId = selected?.IsDefault == true
                ? string.Empty
                : monitorId;
            Controller.NotifySettingsChanged();
        }
    }

    private void AudioErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        Controller.DismissError();
}

using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using VoxLink.UI.Core.Models;
using VoxLink.UI.Core.ViewModels;
using Windows.System;

namespace VoxLink.UI.Pages;

public sealed partial class VRChatPage : Page
{
    private bool _loading = true;

    public VRChatPage()
    {
        InitializeComponent();
        Loaded += VRChatPage_Loaded;
        Unloaded += VRChatPage_Unloaded;
    }

    public AppController Controller => App.Controller;

    private void VRChatPage_Loaded(object sender, RoutedEventArgs args)
    {
        LoadSettingsIntoControls();
        Controller.PropertyChanged += Controller_PropertyChanged;
        RefreshState();
    }

    private void VRChatPage_Unloaded(object sender, RoutedEventArgs args) =>
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
            OscPortNumberBox.Value = Controller.Settings.VrChatOscPort;
            OscListenPortNumberBox.Value = Controller.Settings.VrChatOscListenPort;
            WidthSlider.Value = Controller.Settings.VrOverlayWidthMeters;
            DistanceSlider.Value = Controller.Settings.VrOverlayDistanceMeters;
            VerticalSlider.Value = Controller.Settings.VrOverlayVerticalOffsetMeters;
            VoiceTranslationSwitch.IsOn = Controller.IsVoiceMode;
            VoiceOutputBox.SelectedValue = Controller.Settings.VoiceOutputDeviceId;
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
        MuteSelfEndpoint.Visibility = Controller.Settings.VrChatMuteSelfEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        VoiceRoutePanel.Visibility = Controller.IsVoiceMode ? Visibility.Visible : Visibility.Collapsed;
        VoiceTranslationSwitch.IsEnabled = !Controller.IsRunning;
        VoiceOutputBox.IsEnabled = !Controller.IsRunning && !Controller.IsBusy;
        SpeechContentButtons.IsEnabled = !Controller.IsRunning;
        RefreshVoiceDevicesButton.IsEnabled = !Controller.IsRunning && !Controller.IsBusy;
        TestVoiceOutputButton.IsEnabled = Controller.EngineConnected
            && Controller.IsVoiceRouteReady
            && !Controller.IsBusy;
        VoiceRouteInfoBar.Severity = Controller.IsVoiceRouteReady
            ? InfoBarSeverity.Success
            : InfoBarSeverity.Warning;
        VoiceRouteInfoBar.Title = Controller.IsVoiceRouteReady
            ? "语音路由已就绪"
            : "需要虚拟声卡";
        VoiceRouteInfoBar.Message = Controller.VoiceRouteStatus;
        WidthValueText.Text = $"{Controller.Settings.VrOverlayWidthMeters:0.0} m";
        DistanceValueText.Text = $"{Controller.Settings.VrOverlayDistanceMeters:0.0} m";
        VerticalValueText.Text = $"{Controller.Settings.VrOverlayVerticalOffsetMeters:+0.00;-0.00;0.00} m";
        VrChatErrorBar.Message = Controller.ErrorMessage ?? string.Empty;
        VrChatErrorBar.IsOpen = !string.IsNullOrWhiteSpace(Controller.ErrorMessage);
    }

    private void OscPortNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue))
        {
            return;
        }

        Controller.Settings.VrChatOscPort = (int)Math.Round(args.NewValue);
    }
    private void OscListenPortNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue))
        {
            return;
        }

        Controller.Settings.VrChatOscListenPort = (int)Math.Round(args.NewValue);
    }

    private void VoiceTranslationSwitch_Toggled(object sender, RoutedEventArgs args)
    {
        if (_loading)
        {
            return;
        }

        Controller.ApplyQuickStartMode(VoiceTranslationSwitch.IsOn
            ? QuickStartMode.VrChatVoice
            : QuickStartMode.OscText);
        LoadSettingsIntoControls();
        RefreshState();
    }

    private void VoiceOutputBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || VoiceOutputBox.SelectedValue is not string deviceId)
        {
            return;
        }

        Controller.Settings.VoiceOutputDeviceId = deviceId;
        Controller.NotifySettingsChanged();
        RefreshState();
    }

    private async void RefreshVoiceDevices_Click(object sender, RoutedEventArgs args)
    {
        await Controller.RefreshDevicesAsync();
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

    private void OpenOnboarding_Click(object sender, RoutedEventArgs args) => Controller.RequestOnboarding();

    private async void OpenVirtualCableDownload_Click(object sender, RoutedEventArgs args) =>
        await Launcher.LaunchUriAsync(new Uri("https://vb-audio.com/Cable/"));

    private async void TestVoiceOutput_Click(object sender, RoutedEventArgs args) =>
        await Controller.TestVoiceOutputAsync();

    private void MuteSelfSwitch_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_loading)
        {
            RefreshState();
        }
    }
    private void OverlaySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_loading)
        {
            return;
        }

        Controller.Settings.VrOverlayWidthMeters = WidthSlider.Value;
        Controller.Settings.VrOverlayDistanceMeters = DistanceSlider.Value;
        Controller.Settings.VrOverlayVerticalOffsetMeters = VerticalSlider.Value;
        RefreshState();
    }

    private async void TestOsc_Click(object sender, RoutedEventArgs args) =>
        await Controller.TestVrChatOscAsync();

    private async void TestVrOverlay_Click(object sender, RoutedEventArgs args) =>
        await Controller.TestVrOverlayAsync();

    private void VrChatErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        Controller.DismissError();
}

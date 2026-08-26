using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoxLink.UI.Core.Models;
using VoxLink.UI.Core.ViewModels;
using Windows.System;

namespace VoxLink.UI.Pages;

public sealed partial class SpeechPage : Page
{
    private bool _loading = true;

    public SpeechPage()
    {
        InitializeComponent();
        Loaded += SpeechPage_Loaded;
        Unloaded += SpeechPage_Unloaded;
    }

    public AppController Controller => App.Controller;

    private void SpeechPage_Loaded(object sender, RoutedEventArgs args)
    {
        VoxLink.UI.Infrastructure.ComboBoxPopupPlacer.Apply(this);
        Controller.RenderDevices.CollectionChanged += RenderDevices_CollectionChanged;
        LoadSettingsIntoControls();
        Controller.PropertyChanged += Controller_PropertyChanged;
        RefreshState();
    }

    private void SpeechPage_Unloaded(object sender, RoutedEventArgs args)
    {
        Controller.RenderDevices.CollectionChanged -= RenderDevices_CollectionChanged;
        Controller.PropertyChanged -= Controller_PropertyChanged;
    }

    private void Controller_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppController.Settings))
        {
            LoadSettingsIntoControls();
        }

        if (args.PropertyName is nameof(AppController.Settings)
            or nameof(AppController.IsVoiceRouteReady)
            or nameof(AppController.VoiceRouteStatus)
            or nameof(AppController.IsRunning)
            or nameof(AppController.IsBusy)
            or nameof(AppController.ErrorMessage)
            or nameof(AppController.TestResultMessage))
        {
            RefreshState();
        }
    }

    private void LoadSettingsIntoControls()
    {
        _loading = true;
        try
        {
            Bindings.Update();
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

    private void RenderDevices_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        // 设备列表异步到达后重新套用已保存的语音输出选择，避免显示为空。
        _loading = true;
        try
        {
            VoiceOutputBox.SelectedValue = Controller.Settings.VoiceOutputDeviceId;
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshState()
    {
        var speakMyTranslation = Controller.Settings.SpeakMyTranslation;
        SpeechContentButtons.IsEnabled = speakMyTranslation;
        VoiceOutputBox.IsEnabled = speakMyTranslation && !Controller.IsRunning && !Controller.IsBusy;
        RefreshVoiceDevicesButton.IsEnabled = speakMyTranslation && !Controller.IsRunning && !Controller.IsBusy;
        TestVoiceOutputButton.IsEnabled = speakMyTranslation
            && Controller.EngineConnected
            && Controller.IsVoiceRouteReady
            && !Controller.IsBusy;
        VoiceRouteInfoBar.Severity = !speakMyTranslation
            ? InfoBarSeverity.Informational
            : Controller.IsVoiceRouteReady
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Warning;
        VoiceRouteInfoBar.Title = !speakMyTranslation
            ? "未开启朗读我的译文"
            : Controller.IsVoiceRouteReady
                ? "语音路由已就绪"
                : "需要虚拟声卡";
        VoiceRouteInfoBar.Message = Controller.VoiceRouteStatus;
        SpeechErrorBar.Message = Controller.ErrorMessage ?? string.Empty;
        SpeechErrorBar.IsOpen = !string.IsNullOrWhiteSpace(Controller.ErrorMessage);
        RefreshSpeechRefinementHint();
    }

    private void SpeakMyTranslationSwitch_Toggled(object sender, RoutedEventArgs args)
    {
        if (_loading)
        {
            return;
        }

        if (SpeakMyTranslationSwitch.IsOn)
        {
            Controller.EnsureVirtualCableSelected();
        }

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

    private void SpeechRefinementSwitch_Toggled(object sender, RoutedEventArgs args) =>
        RefreshSpeechRefinementHint();

    private void RefreshSpeechRefinementHint() =>
        SpeechRefinementHintBar.IsOpen = SpeechRefinementSwitch.IsOn
            && (!Controller.Settings.UseAiTranslation || !Controller.Settings.SupportsGeneration);

    private async void OpenVirtualCableDownload_Click(object sender, RoutedEventArgs args) =>
        await Launcher.LaunchUriAsync(new Uri("https://vb-audio.com/Cable/"));

    private async void TestVoiceOutput_Click(object sender, RoutedEventArgs args) =>
        await Controller.TestVoiceOutputAsync();

    private void SpeechErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        Controller.DismissError();
}

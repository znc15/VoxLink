using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using VoxLink.UI.Core.Models;
using VoxLink.UI.Core.ViewModels;

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

    private void VRChatPage_Unloaded(object sender, RoutedEventArgs args)
    {
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
            OscPortNumberBox.Value = Controller.Settings.VrChatOscPort;
            OscListenPortNumberBox.Value = Controller.Settings.VrChatOscListenPort;
            SpeakerLabelSwitch.IsOn = Controller.Settings.SpeakerLabelMode != SpeakerLabelMode.Off;
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
        LocalSpeakerInfo.IsOpen = Controller.Settings.SpeakerLabelMode != SpeakerLabelMode.Off;
        RestartHintBar.IsOpen = Controller.NeedsSessionRestart;
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

    private void SpeakerLabelSwitch_Toggled(object sender, RoutedEventArgs args)
    {
        if (_loading)
        {
            return;
        }

        Controller.Settings.SpeakerLabelMode = SpeakerLabelSwitch.IsOn
            ? SpeakerLabelMode.Local
            : SpeakerLabelMode.Off;
        RefreshState();
    }

    private void MuteSelfSwitch_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_loading)
        {
            RefreshState();
        }
    }

    private async void TestOsc_Click(object sender, RoutedEventArgs args) =>
        await Controller.TestVrChatOscAsync();

    private void VrChatErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        Controller.DismissError();
}

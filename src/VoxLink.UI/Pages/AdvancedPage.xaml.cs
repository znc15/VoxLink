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
}

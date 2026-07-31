using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
        LoadSettingsIntoControls();
        Controller.PropertyChanged += Controller_PropertyChanged;
        RefreshState();
    }
    private void AudioPage_Unloaded(object sender, RoutedEventArgs args) =>
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
            WhisperModelButtons.SelectedItem = Controller.Settings.WhisperModel;
        }
        finally
        {
            _loading = false;
        }
    }
    private void RefreshState()
    {
        LocalWhisperPanel.Visibility = Controller.Settings.UsesCloudAsr
            ? Visibility.Collapsed
            : Visibility.Visible;
        ThresholdValueText.Text = Controller.Settings.VoiceThreshold.ToString("0.000");
        SilenceValueText.Text = $"{Controller.Settings.SilenceDurationMs} ms";
        var hasProgress = !string.IsNullOrWhiteSpace(Controller.ModelStatus);
        ModelProgressBar.Visibility = hasProgress ? Visibility.Visible : Visibility.Collapsed;
        ModelProgressText.Visibility = hasProgress ? Visibility.Visible : Visibility.Collapsed;
        ModelProgressBar.Value = Controller.ModelProgress;
        ModelProgressText.Text = hasProgress
            ? $"{Controller.ModelStatus} · {Controller.ModelProgress:P0}"
            : string.Empty;
        AudioErrorBar.Message = Controller.ErrorMessage ?? string.Empty;
        AudioErrorBar.IsOpen = !string.IsNullOrWhiteSpace(Controller.ErrorMessage);
        RestartHintBar.IsOpen = Controller.NeedsSessionRestart;
    }

    private void Device_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_loading)
        {
            Controller.NotifySettingsChanged();
        }
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs args) =>
        await Controller.RefreshDevicesAsync();

    private async void PrepareModel_Click(object sender, RoutedEventArgs args) =>
        await Controller.PrepareModelAsync();

    private void WhisperModelButtons_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_loading && WhisperModelButtons.SelectedItem is string model)
        {
            Controller.Settings.WhisperModel = model;
        }
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
    private void AudioErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        Controller.DismissError();
}

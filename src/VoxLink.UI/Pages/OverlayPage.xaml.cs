using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using VoxLink.UI.Core.ViewModels;

namespace VoxLink.UI.Pages;

public sealed partial class OverlayPage : Page
{
    private bool _loading = true;

    public OverlayPage()
    {
        InitializeComponent();
        Loaded += OverlayPage_Loaded;
        Unloaded += OverlayPage_Unloaded;
    }

    public AppController Controller => App.Controller;

    private void OverlayPage_Loaded(object sender, RoutedEventArgs args)
    {
        LoadSettingsIntoControls();
        Controller.PropertyChanged += Controller_PropertyChanged;
        RefreshState();
    }

    private void OverlayPage_Unloaded(object sender, RoutedEventArgs args)
    {
        Controller.PropertyChanged -= Controller_PropertyChanged;
    }

    private void Controller_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppController.Settings))
        {
            LoadSettingsIntoControls();
        }

        if (args.PropertyName is nameof(AppController.ErrorMessage)
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
            WidthSlider.Value = Controller.Settings.VrOverlayWidthMeters;
            DistanceSlider.Value = Controller.Settings.VrOverlayDistanceMeters;
            VerticalSlider.Value = Controller.Settings.VrOverlayVerticalOffsetMeters;
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshState()
    {
        WidthValueText.Text = $"{Controller.Settings.VrOverlayWidthMeters:0.0} m";
        DistanceValueText.Text = $"{Controller.Settings.VrOverlayDistanceMeters:0.0} m";
        VerticalValueText.Text = $"{Controller.Settings.VrOverlayVerticalOffsetMeters:+0.00;-0.00;0.00} m";
        OverlayErrorBar.Message = Controller.ErrorMessage ?? string.Empty;
        OverlayErrorBar.IsOpen = !string.IsNullOrWhiteSpace(Controller.ErrorMessage);
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

    private async void TestDesktopOverlay_Click(object sender, RoutedEventArgs args) =>
        await Controller.TestDesktopOverlayAsync();

    private async void TestVrOverlay_Click(object sender, RoutedEventArgs args) =>
        await Controller.TestVrOverlayAsync();

    private void OverlayErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        Controller.DismissError();
}

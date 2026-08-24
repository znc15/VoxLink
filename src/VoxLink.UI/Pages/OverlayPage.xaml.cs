using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using VoxLink.UI.Core.Models;
using VoxLink.UI.Core.ViewModels;

namespace VoxLink.UI.Pages;

public sealed partial class OverlayPage : Page
{
    private const double DefaultDesktopOverlayWidth = 760;

    private bool _loading = true;
    private AppSettings? _subscribedSettings;

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
        AttachSettingsListeners();
        RefreshState();
    }

    private void OverlayPage_Unloaded(object sender, RoutedEventArgs args)
    {
        Controller.PropertyChanged -= Controller_PropertyChanged;
        DetachSettingsListeners();
    }

    private void AttachSettingsListeners()
    {
        if (ReferenceEquals(_subscribedSettings, Controller.Settings))
        {
            return;
        }

        DetachSettingsListeners();
        _subscribedSettings = Controller.Settings;
        _subscribedSettings.PropertyChanged += Settings_PropertyChanged;
    }

    private void DetachSettingsListeners()
    {
        if (_subscribedSettings is null)
        {
            return;
        }

        _subscribedSettings.PropertyChanged -= Settings_PropertyChanged;
        _subscribedSettings = null;
    }

    private void Controller_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppController.Settings))
        {
            LoadSettingsIntoControls();
            AttachSettingsListeners();
        }

        if (args.PropertyName is nameof(AppController.ErrorMessage)
            or nameof(AppController.TestResultMessage))
        {
            RefreshState();
        }
    }

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(AppSettings.DesktopOverlayWidth)
            or nameof(AppSettings.DesktopOverlayFontSize)))
        {
            return;
        }

        // 悬浮窗拖动/拉伸的回写刷新滑块；LoadSettingsIntoControls 内部的 _loading 标志
        // 会阻止滑块再次写回设置，避免回环。
        LoadSettingsIntoControls();
        RefreshState();
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
            DesktopWidthSlider.Value = Controller.Settings.DesktopOverlayWidth ?? DefaultDesktopOverlayWidth;
            DesktopFontSizeSlider.Value = Controller.Settings.DesktopOverlayFontSize;
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
        DesktopWidthValueText.Text =
            $"{(int)(Controller.Settings.DesktopOverlayWidth ?? DefaultDesktopOverlayWidth)} 像素";
        DesktopFontSizeValueText.Text = $"{Controller.Settings.DesktopOverlayFontSize} 号";
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

    private void DesktopSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_loading)
        {
            return;
        }

        Controller.Settings.DesktopOverlayWidth = DesktopWidthSlider.Value;
        Controller.Settings.DesktopOverlayFontSize = (int)DesktopFontSizeSlider.Value;
        RefreshState();
    }

    private void ResetDesktopOverlay_Click(object sender, RoutedEventArgs args)
    {
        Controller.ResetDesktopOverlayPlacement();
        LoadSettingsIntoControls();
        RefreshState();
    }

    private async void TestDesktopOverlay_Click(object sender, RoutedEventArgs args) =>
        await Controller.TestDesktopOverlayAsync();

    private async void TestVrOverlay_Click(object sender, RoutedEventArgs args) =>
        await Controller.TestVrOverlayAsync();

    private void OverlayErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        Controller.DismissError();
}

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
        VoxLink.UI.Infrastructure.ComboBoxPopupPlacer.Apply(this);
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
            or nameof(AppSettings.DesktopOverlayFontSize)
            or nameof(AppSettings.DesktopOverlayDisplayMode)
            or nameof(AppSettings.DesktopOverlayAutoHideSeconds)))
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
            SelectByTag(DesktopDisplayModeBox, Controller.Settings.DesktopOverlayDisplayMode.ToString());
            AutoHideSecondsPanel.Visibility = Controller.Settings.DesktopOverlayDisplayMode
                == DesktopOverlayDisplayMode.AutoHide
                ? Visibility.Visible
                : Visibility.Collapsed;
            DesktopAutoHideSlider.Value = Controller.Settings.DesktopOverlayAutoHideSeconds;
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
        DesktopAutoHideValueText.Text = $"{Controller.Settings.DesktopOverlayAutoHideSeconds} 秒";
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

    private void DesktopDisplayModeBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || !TryReadTag(DesktopDisplayModeBox, out var tag)
            || !Enum.TryParse<DesktopOverlayDisplayMode>(tag, out var mode))
        {
            return;
        }

        Controller.Settings.DesktopOverlayDisplayMode = mode;
        AutoHideSecondsPanel.Visibility = mode == DesktopOverlayDisplayMode.AutoHide
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void DesktopAutoHideSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_loading)
        {
            return;
        }

        Controller.Settings.DesktopOverlayAutoHideSeconds = (int)DesktopAutoHideSlider.Value;
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

    private static void SelectByTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items)
        {
            if (item is ComboBoxItem { Tag: string itemTag }
                && itemTag.Equals(tag, StringComparison.Ordinal))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = -1;
    }

    private static bool TryReadTag(ComboBox comboBox, out string tag)
    {
        if (comboBox.SelectedItem is ComboBoxItem { Tag: string value })
        {
            tag = value;
            return true;
        }

        tag = string.Empty;
        return false;
    }
}

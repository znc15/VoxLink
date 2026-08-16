using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using VoxLink.UI.Core.ViewModels;

namespace VoxLink.UI.Pages;

public sealed partial class AdvancedPage : Page
{
    private bool _loading;

    public AdvancedPage()
    {
        InitializeComponent();
        Loaded += AdvancedPage_Loaded;
        Unloaded += AdvancedPage_Unloaded;
    }

    public AppController Controller => App.Controller;

    private void AdvancedPage_Loaded(object sender, RoutedEventArgs args)
    {
        _loading = true;
        try
        {
            Bindings.Update();
            WindowOpacitySlider.Value = Controller.Settings.WindowOpacity;
        }
        finally
        {
            _loading = false;
        }

        RefreshOpacityLabel();
        Controller.PropertyChanged += Controller_PropertyChanged;
    }

    private void AdvancedPage_Unloaded(object sender, RoutedEventArgs args) =>
        Controller.PropertyChanged -= Controller_PropertyChanged;

    private void Controller_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppController.Settings))
        {
            _loading = true;
            try
            {
                Bindings.Update();
                WindowOpacitySlider.Value = Controller.Settings.WindowOpacity;
            }
            finally
            {
                _loading = false;
            }

            RefreshOpacityLabel();
        }
    }

    private void WindowOpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_loading)
        {
            return;
        }

        Controller.Settings.WindowOpacity = WindowOpacitySlider.Value;
        RefreshOpacityLabel();
    }

    private void RefreshOpacityLabel() =>
        OpacityValueText.Text = $"{Controller.Settings.WindowOpacity:P0}";
}

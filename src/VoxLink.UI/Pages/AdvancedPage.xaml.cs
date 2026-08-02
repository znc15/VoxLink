using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoxLink.UI.Core.ViewModels;

namespace VoxLink.UI.Pages;

public sealed partial class AdvancedPage : Page
{
    public AdvancedPage()
    {
        InitializeComponent();
        Loaded += AdvancedPage_Loaded;
        Unloaded += AdvancedPage_Unloaded;
    }

    public AppController Controller => App.Controller;

    private void AdvancedPage_Loaded(object sender, RoutedEventArgs args)
    {
        Bindings.Update();
        Controller.PropertyChanged += Controller_PropertyChanged;
    }

    private void AdvancedPage_Unloaded(object sender, RoutedEventArgs args) =>
        Controller.PropertyChanged -= Controller_PropertyChanged;

    private void Controller_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppController.Settings))
        {
            Bindings.Update();
        }
    }
}

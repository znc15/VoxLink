using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoxLink.UI.Core.ViewModels;

namespace VoxLink.UI.Pages;

public sealed partial class ProvidersPage : Page
{
    public ProvidersPage()
    {
        InitializeComponent();
        Loaded += ProvidersPage_Loaded;
        Unloaded += ProvidersPage_Unloaded;
    }

    public AppController Controller => App.Controller;

    private void ProvidersPage_Loaded(object sender, RoutedEventArgs args)
    {
        Bindings.Update();
        Controller.PropertyChanged += Controller_PropertyChanged;
        RefreshState();
    }

    private void ProvidersPage_Unloaded(object sender, RoutedEventArgs args) =>
        Controller.PropertyChanged -= Controller_PropertyChanged;

    private void Controller_PropertyChanged(object? sender, PropertyChangedEventArgs args) =>
        RefreshState();

    private void RefreshState()
    {
        ProviderResultBar.Message = Controller.TestResultMessage ?? string.Empty;
        ProviderResultBar.IsOpen = !string.IsNullOrWhiteSpace(Controller.TestResultMessage);
        ProviderErrorBar.Message = Controller.ErrorMessage ?? string.Empty;
        ProviderErrorBar.IsOpen = !string.IsNullOrWhiteSpace(Controller.ErrorMessage);
    }

    private async void TestTranslation_Click(object sender, RoutedEventArgs args)
    {
        TranslationTestProgress.Visibility = Visibility.Visible;
        TranslationTestProgress.IsActive = true;
        try
        {
            await Controller.TestTranslationAsync();
        }
        finally
        {
            TranslationTestProgress.IsActive = false;
            TranslationTestProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void TestAsr_Click(object sender, RoutedEventArgs args)
    {
        AsrTestProgress.Visibility = Visibility.Visible;
        AsrTestProgress.IsActive = true;
        try
        {
            await Controller.PrepareModelAsync();
        }
        finally
        {
            AsrTestProgress.IsActive = false;
            AsrTestProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void TestSpeech_Click(object sender, RoutedEventArgs args)
    {
        SpeechTestProgress.Visibility = Visibility.Visible;
        SpeechTestProgress.IsActive = true;
        try
        {
            await Controller.TestSpeechAsync();
        }
        finally
        {
            SpeechTestProgress.IsActive = false;
            SpeechTestProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void ProviderErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        Controller.DismissError();
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoxLink.UI.Core.Models;
using VoxLink.UI.Core.ViewModels;

namespace VoxLink.UI.Pages;

public sealed partial class LocalModelsPage : Page
{
    public LocalModelsPage()
    {
        InitializeComponent();
        Loaded += LocalModelsPage_Loaded;
        Unloaded += LocalModelsPage_Unloaded;
    }

    public AppController Controller => App.Controller;

    private void LocalModelsPage_Loaded(object sender, RoutedEventArgs args)
    {
        Controller.PropertyChanged += Controller_PropertyChanged;
        RefreshState();
    }

    private void LocalModelsPage_Unloaded(object sender, RoutedEventArgs args) =>
        Controller.PropertyChanged -= Controller_PropertyChanged;

    private void Controller_PropertyChanged(object? sender, PropertyChangedEventArgs args) => RefreshState();

    private void RefreshState()
    {
        ModelResultBar.Message = Controller.LocalModelResultMessage ?? string.Empty;
        ModelResultBar.IsOpen = !string.IsNullOrWhiteSpace(Controller.LocalModelResultMessage);
        ModelErrorBar.Message = Controller.ErrorMessage ?? string.Empty;
        ModelErrorBar.IsOpen = !string.IsNullOrWhiteSpace(Controller.ErrorMessage);
        RestartInfoBar.IsOpen = Controller.NeedsSessionRestart;
        RefreshLocalModelsButton.IsEnabled = !Controller.IsBusy && !Controller.HasBusyLocalModels;
        InstallRecommendedButton.IsEnabled =
            !Controller.IsRunning && !Controller.IsBusy && !Controller.HasBusyLocalModels;
        InstallRecommendedButton.Content = Controller.RecommendedLocalModelsReady
            ? "启用并启动"
            : "一键安装并启动";
    }

    private async void RefreshLocalModels_Click(object sender, RoutedEventArgs args) =>
        await Controller.RefreshLocalModelsAsync();

    private async void ModelPrimaryAction_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: LocalModelItem model })
        {
            await Controller.InstallAndActivateLocalModelAsync(model.Id);
        }
    }

    private async void InstallRecommended_Click(object sender, RoutedEventArgs args) =>
        await Controller.InstallRecommendedLocalModelsAsync(startSession: true);

    private async void ModelSettings_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { DataContext: LocalModelItem model } || XamlRoot is null)
        {
            return;
        }

        var content = CreateModelSettingsContent(model);
        var dialog = new ContentDialog
        {
            Title = model.Name,
            Content = content,
            PrimaryButtonText = model.Id == LocalModelIds.Kokoro82M ? "保存" : "关闭",
            SecondaryButtonText = model.Installed ? "删除" : string.Empty,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        try
        {
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && model.Id == LocalModelIds.Kokoro82M)
            {
                ApplyModelSettings(content);
                await Controller.SaveCommittedServiceSettingsAsync(reportToLocalModels: true);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await ConfirmAndRemoveAsync(model);
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }
    }

    private FrameworkElement CreateModelSettingsContent(LocalModelItem model)
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{model.CategoryLabel} · {model.InstallStateLabel} · {model.DownloadSizeLabel}",
            TextWrapping = TextWrapping.Wrap
        });

        if (model.Id == LocalModelIds.Kokoro82M)
        {
            panel.Children.Add(new NumberBox
            {
                Header = "音色 ID",
                Minimum = 0,
                Maximum = 102,
                Value = Controller.Settings.KokoroSpeakerId,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                Tag = "speaker"
            });
            panel.Children.Add(new NumberBox
            {
                Header = "语速",
                Minimum = 0.5,
                Maximum = 2,
                SmallChange = 0.1,
                Value = Controller.Settings.KokoroSpeed,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                Tag = "speed"
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = model.Requirements,
            Style = (Style)Application.Current.Resources["CaptionStyle"],
            TextWrapping = TextWrapping.Wrap
        });
        return panel;
    }

    private void ApplyModelSettings(FrameworkElement content)
    {
        if (content is not StackPanel panel)
        {
            return;
        }

        foreach (var child in panel.Children)
        {
            switch (child)
            {
                case NumberBox { Tag: "speaker" } speaker when double.IsFinite(speaker.Value):
                    Controller.Settings.KokoroSpeakerId = (int)Math.Round(speaker.Value);
                    break;
                case NumberBox { Tag: "speed" } speed when double.IsFinite(speed.Value):
                    Controller.Settings.KokoroSpeed = speed.Value;
                    break;
            }
        }
    }

    private async Task ConfirmAndRemoveAsync(LocalModelItem model)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var confirmation = new ContentDialog
        {
            Title = $"删除 {model.Name}？",
            Content = "模型文件将从本机删除。正在运行时不会删除当前模型。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await confirmation.ShowAsync() == ContentDialogResult.Primary)
        {
            await Controller.RemoveLocalModelWithFallbackAsync(model.Id);
        }
    }

    private void ModelErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        Controller.DismissError();
}

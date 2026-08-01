using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoxLink.UI.Core.ViewModels;

namespace VoxLink.UI.Pages;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        Loaded += AboutPage_Loaded;
        Unloaded += AboutPage_Unloaded;
    }

    public AppController Controller => App.Controller;

    private void AboutPage_Loaded(object sender, RoutedEventArgs args)
    {
        Controller.PropertyChanged += Controller_PropertyChanged;
        RefreshState();
    }

    private void AboutPage_Unloaded(object sender, RoutedEventArgs args) =>
        Controller.PropertyChanged -= Controller_PropertyChanged;

    private void Controller_PropertyChanged(object? sender, PropertyChangedEventArgs args) =>
        RefreshState();

    private void RefreshState()
    {
        VersionText.Text = $"VoxLink {Controller.AppVersion.ToString(3)}";
        EngineStateText.Text = Controller.EngineConnected ? "已连接" : "未连接";
        SessionStateText.Text = Controller.IsRunning ? "运行中" : "已停止";
        ActivityText.Text = Controller.Activity switch
        {
            "listening" => "正在监听",
            "transcribing" => "正在识别",
            "translating" => "正在翻译",
            "speaking" => "正在播放",
            "error" => "异常",
            "preparing" => "准备中",
            _ => "空闲"
        };
        UpdateStatusText.Text = Controller.UpdateStatusText ?? string.Empty;
        CheckUpdatesButton.IsEnabled = !Controller.IsCheckingForUpdates;
        OpenReleaseButton.Visibility = Controller.IsUpdateAvailable
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs args) =>
        await Controller.CheckForUpdatesAsync();

    private void OpenRelease_Click(object sender, RoutedEventArgs args) =>
        Controller.OpenLatestReleasePage();
}

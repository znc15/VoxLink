using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoxLink.UI.Core.ViewModels;

namespace VoxLink.UI.Pages;

/// <summary>调用 Windows 系统文件管理器（文件夹选择器）选取本地模型存储位置。</summary>

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

    private async void BrowseLocalModelDirectory_Click(object sender, RoutedEventArgs args) =>
        await PickDirectoryAsync(path => Controller.Settings.LocalModelDirectory = path);

    private async void BrowseManagedRuntimeDirectory_Click(object sender, RoutedEventArgs args) =>
        await PickDirectoryAsync(path => Controller.Settings.ManagedRuntimeDirectory = path);

    private async Task PickDirectoryAsync(Action<string> applyPath)
    {
        if (App.MainWindow is null)
        {
            return;
        }

        // 经典 UWP FolderPicker + InitializeWithWindow：非打包 WinUI 3 应用
        // 没有包标识，必须显式绑定宿主窗口句柄后才能显示系统文件夹选择器。
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        try
        {
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null && !string.IsNullOrWhiteSpace(folder.Path))
            {
                applyPath(folder.Path.Trim());
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }
    }
}

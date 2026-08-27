using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using VoxLink.UI.Core.Services;
using VoxLink.UI.Core.ViewModels;
using VoxLink.UI.Infrastructure;

namespace VoxLink.UI;

public partial class App : Application
{
    // Shared with the retired WPF debug entry so only one frontend can own local model files.
    private const string MutexName = "Local\\VoxLink.Frontend.Singleton";
    private Mutex? _singleInstanceMutex;
    private Window? _window;

    /// <summary>当前主窗口实例，供系统文件/文件夹选择器等需要窗口句柄的 API 使用。</summary>
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    public static AppController Controller { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            Exit();
            return;
        }

        var dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("无法获取 WinUI 调度队列。");
        Controller = new AppController(
            new EngineClient(),
            new SettingsRepository(),
            new Infrastructure.DispatcherQueueSynchronizationContext(dispatcher),
            autoCheckForUpdates: true);
        _window = new MainWindow();
        MainWindow = (MainWindow)_window;
        _window.Activate();
        _ = Controller.InitializeAsync();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        args.Handled = true;
        if (Controller is not null)
        {
            LogService.Instance.Error("UI", args.Exception, "未处理异常");
            System.Diagnostics.Debug.WriteLine(args.Exception);
        }
    }
}

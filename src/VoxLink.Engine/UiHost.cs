using System.Windows;
using System.Windows.Threading;
using VoxLink.Models;
using VoxLink.Services;

namespace VoxLink.Engine;

internal sealed class UiHost : IDisposable
{
    private readonly Action<string> _hotkeyCallback;
    private readonly Action<string, object>? _eventCallback;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private Application? _application;
    private Window? _messageWindow;
    private OverlayWindow? _overlay;
    private SteamVrOverlayHost? _steamVrOverlay;
    private GlobalHotkeyService? _hotkeys;
    private Exception? _startupError;
    private volatile bool _disposed;

    public UiHost(
        Action<string> hotkeyCallback,
        Action<string, object>? eventCallback = null)
    {
        _hotkeyCallback = hotkeyCallback;
        _eventCallback = eventCallback;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "VoxLink.Engine.UI"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("Windows 桌面宿主启动超时。");
        }

        if (_startupError is not null)
        {
            throw new InvalidOperationException("Windows 桌面宿主启动失败。", _startupError);
        }
    }

    public void Configure(AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var dispatcher = GetDispatcher();
        dispatcher.Invoke(() =>
        {
            _overlay!.SetEnabled(settings.ShowOverlay);
            _overlay.Configure(
                settings.DesktopOverlayLeft,
                settings.DesktopOverlayTop,
                settings.DesktopOverlayWidth,
                settings.DesktopOverlayHeight,
                settings.DesktopOverlayFontSize,
                settings.DesktopOverlayTopmost,
                settings.DesktopOverlayLockPosition);
            _steamVrOverlay!.Configure(
                settings.ShowVrOverlay,
                settings.VrOverlayWidthMeters,
                settings.VrOverlayDistanceMeters,
                settings.VrOverlayVerticalOffsetMeters);
            try
            {
                _hotkeys!.Register(settings.ToggleHotkey, settings.TranslateHotkey);
            }
            catch (Exception exception)
            {
                // 快捷键非法或被占用不应阻断引擎；降级为不注册全局快捷键，写入诊断日志。
                Console.Error.WriteLine($"全局快捷键注册失败（已跳过，请在设置中重新录制）：{exception.Message}");
            }
        });
    }

    public void ShowSubtitle(ConversationMessage message)
    {
        if (_disposed)
        {
            return;
        }

        GetDispatcher().BeginInvoke(() =>
        {
            _overlay?.ShowSubtitle(message);
            _steamVrOverlay?.ShowSubtitle(message);
        });
    }

    public string TestVrOverlay()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GetDispatcher().Invoke(() =>
            _steamVrOverlay?.ShowTest() ?? "SteamVR 字幕宿主未就绪");
    }

    public string TestDesktopOverlay()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GetDispatcher().Invoke(() =>
            _overlay?.ShowTest() ?? "桌面字幕宿主未就绪");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_application is not null)
        {
            try
            {
                GetDispatcher().Invoke(() =>
                {
                    _hotkeys?.Dispose();
                    _hotkeys = null;
                    _steamVrOverlay?.Dispose();
                    _steamVrOverlay = null;
                    _overlay?.Close();
                    _overlay = null;
                    _messageWindow?.Close();
                    _messageWindow = null;
                    _application.Shutdown();
                });
            }
            catch (TaskCanceledException)
            {
            }
        }

        _thread.Join(TimeSpan.FromSeconds(5));
        _ready.Dispose();
    }

    private Dispatcher GetDispatcher() => _application?.Dispatcher
        ?? throw new InvalidOperationException("Windows 桌面宿主尚未就绪。");

    private void Run()
    {
        try
        {
            _application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            _messageWindow = new Window
            {
                Width = 1,
                Height = 1,
                Left = -10_000,
                Top = -10_000,
                Opacity = 0,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };
            _messageWindow.Show();
            _hotkeys = new GlobalHotkeyService(_messageWindow);
            _hotkeys.ToggleRequested += (_, _) => _hotkeyCallback("toggle");
            _hotkeys.TranslateRequested += (_, _) => _hotkeyCallback("translate");
            _overlay = new OverlayWindow();
            if (_eventCallback is not null)
            {
                _overlay.PlacementChanged += (left, top, width, height) =>
                    _eventCallback("overlayPlacement", new { left, top, width, height });
            }
            _steamVrOverlay = new SteamVrOverlayHost();
            _ready.Set();
            _application.Run();
        }
        catch (Exception exception)
        {
            _startupError = exception;
            _ready.Set();
        }
    }
}

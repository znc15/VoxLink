using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace VoxLink.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const int ToggleId = 0x1201;
    private const int TranslateId = 0x1202;

    private readonly HwndSource _source;
    private readonly IntPtr _handle;
    private bool _disposed;

    public GlobalHotkeyService(Window window)
    {
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle)
            ?? throw new InvalidOperationException("无法连接窗口消息循环。");
        _source.AddHook(WindowMessageHook);
    }

    public event EventHandler? ToggleRequested;

    public event EventHandler? TranslateRequested;

    public void Register(string toggleHotkey, string translateHotkey)
    {
        Unregister();
        Register(ToggleId, toggleHotkey);
        try
        {
            Register(TranslateId, translateHotkey);
        }
        catch
        {
            UnregisterHotKey(_handle, ToggleId);
            throw;
        }
    }

    public void Unregister()
    {
        UnregisterHotKey(_handle, ToggleId);
        UnregisterHotKey(_handle, TranslateId);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
        _source.RemoveHook(WindowMessageHook);
    }

    public static (uint Modifiers, uint VirtualKey) Parse(string hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            throw new ArgumentException("快捷键不能为空。", nameof(hotkey));
        }

        uint modifiers = ModNoRepeat;
        Key? key = null;
        foreach (var part in hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= ModControl;
                    break;
                case "ALT":
                    modifiers |= ModAlt;
                    break;
                case "SHIFT":
                    modifiers |= ModShift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= ModWin;
                    break;
                default:
                    if (!Enum.TryParse<Key>(part, ignoreCase: true, out var parsedKey))
                    {
                        throw new ArgumentException($"无法识别快捷键：{hotkey}", nameof(hotkey));
                    }

                    key = parsedKey;
                    break;
            }
        }

        if (key is null || modifiers == ModNoRepeat)
        {
            throw new ArgumentException("全局快捷键必须包含修饰键和主键。", nameof(hotkey));
        }

        return (modifiers, (uint)KeyInterop.VirtualKeyFromKey(key.Value));
    }

    private void Register(int id, string hotkey)
    {
        var (modifiers, virtualKey) = Parse(hotkey);
        if (!RegisterHotKey(_handle, id, modifiers, virtualKey))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"快捷键 {hotkey} 已被其他程序占用。");
        }
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WmHotkey)
        {
            return IntPtr.Zero;
        }

        switch (wParam.ToInt32())
        {
            case ToggleId:
                ToggleRequested?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;
            case TranslateId:
                TranslateRequested?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}

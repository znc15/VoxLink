using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VoxLink.UI.Infrastructure;

internal sealed class TrayIconService : IDisposable
{
    private const string WindowClassName = "VoxLinkTrayWindow";
    private const uint TrayIconId = 0;
    private const uint TrayCallbackMessage = 0x8001;
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NifMessage = 0x0001;
    private const uint NifIcon = 0x0002;
    private const uint NifTip = 0x0004;
    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x0010;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmNull = 0x0000;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;
    private const uint MfChecked = 0x0008;
    private const uint MfGrayed = 0x0001;
    private const uint MfPopup = 0x0010;

    private readonly string _iconPath;
    private readonly WindowProc _windowProc;
    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _iconAdded;
    private bool _classRegistered;
    private bool _disposed;

    public TrayIconService(string iconPath)
    {
        _iconPath = iconPath;
        _windowProc = WindowProcHandler;
        RegisterWindowClass();

        _hwnd = CreateWindowExW(
            0,
            WindowClassName,
            "VoxLinkTrayWindow",
            0,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error(), "无法创建托盘消息窗口。");
            if (_classRegistered)
            {
                UnregisterClassW(WindowClassName, GetModuleHandle(null));
                _classRegistered = false;
            }

            throw error;
        }
    }

    public event Action? RestoreRequested;

    public Func<IReadOnlyList<TrayMenuItem>>? MenuProvider { get; set; }

    public bool Visible
    {
        get => _iconAdded;
        set
        {
            if (value)
            {
                AddIcon();
            }
            else
            {
                RemoveIcon();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RemoveIcon();
        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_classRegistered)
        {
            UnregisterClassW(WindowClassName, GetModuleHandle(null));
            _classRegistered = false;
        }
    }

    private void RegisterWindowClass()
    {
        var wndClass = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_windowProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = WindowClassName
        };

        _classRegistered = RegisterClassExW(ref wndClass) != 0;
        if (!_classRegistered && Marshal.GetLastWin32Error() != 1410)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册托盘消息窗口类。");
        }
    }

    private void AddIcon()
    {
        if (_iconAdded)
        {
            return;
        }

        if (_icon == IntPtr.Zero)
        {
            _icon = LoadImageW(IntPtr.Zero, _iconPath, ImageIcon, 32, 32, LoadFromFile);
            if (_icon == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载托盘图标。");
            }
        }

        var data = new NativeNotifyIconData
        {
            cbSize = Marshal.SizeOf<NativeNotifyIconData>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = _icon,
            szTip = "VoxLink 正在后台运行，双击打开窗口",
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };

        if (!Shell_NotifyIconW(NimAdd, ref data))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法添加系统托盘图标。");
        }

        _iconAdded = true;
    }

    private void RemoveIcon()
    {
        if (!_iconAdded)
        {
            return;
        }

        var data = new NativeNotifyIconData
        {
            cbSize = Marshal.SizeOf<NativeNotifyIconData>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
        Shell_NotifyIconW(NimDelete, ref data);
        _iconAdded = false;
    }

    private IntPtr WindowProcHandler(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == TrayCallbackMessage)
        {
            switch (lParam.ToInt32())
            {
                case (int)WmLButtonDblClk:
                    RestoreRequested?.Invoke();
                    return IntPtr.Zero;
                case (int)WmRButtonUp:
                    ShowContextMenu();
                    return IntPtr.Zero;
            }
        }

        return DefWindowProcW(hWnd, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        var submenus = new List<IntPtr>();
        var commands = new Dictionary<uint, Action>();
        uint nextCommand = 1;
        try
        {
            AppendMenuItems(
                menu,
                MenuProvider?.Invoke() ?? [],
                submenus,
                commands,
                ref nextCommand);
            GetCursorPos(out var point);
            SetForegroundWindow(_hwnd);
            PostMessageW(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);

            var command = (uint)TrackPopupMenu(
                menu,
                TpmRightButton | TpmReturnCmd,
                point.X,
                point.Y,
                0,
                _hwnd,
                IntPtr.Zero);
            if (commands.TryGetValue(command, out var action))
            {
                action();
            }
        }
        finally
        {
            foreach (var submenu in submenus)
            {
                DestroyMenu(submenu);
            }

            DestroyMenu(menu);
        }
    }

    private void AppendMenuItems(
        IntPtr menu,
        IReadOnlyList<TrayMenuItem> items,
        List<IntPtr> submenus,
        Dictionary<uint, Action> commands,
        ref uint nextCommand)
    {
        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                AppendMenuW(menu, MfSeparator, IntPtr.Zero, null);
                continue;
            }

            var flags = MfString;
            if (item.Checked)
            {
                flags |= MfChecked;
            }

            if (!item.Enabled)
            {
                flags |= MfGrayed;
            }

            if (item.Children is { Count: > 0 })
            {
                var submenu = CreatePopupMenu();
                if (submenu == IntPtr.Zero)
                {
                    continue;
                }

                submenus.Add(submenu);
                AppendMenuW(menu, flags | MfPopup, submenu, item.Text);
                AppendMenuItems(submenu, item.Children, submenus, commands, ref nextCommand);
                continue;
            }

            var commandId = nextCommand++;
            commands[commandId] = item.Command ?? (() => { });
            AppendMenuW(menu, flags, new IntPtr(commandId), item.Text);
        }
    }

    internal sealed record TrayMenuItem(
        string Text = "",
        Action? Command = null,
        bool Checked = false,
        bool Enabled = true,
        IReadOnlyList<TrayMenuItem>? Children = null,
        bool IsSeparator = false);

    private delegate IntPtr WindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeNotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(
        IntPtr hInstance,
        string lpszName,
        uint type,
        int cx,
        int cy,
        uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NativeNotifyIconData lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(
        IntPtr hMenu,
        uint uFlags,
        IntPtr uIDNewItem,
        string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenu(
        IntPtr hMenu,
        uint uFlags,
        int x,
        int y,
        int nReserved,
        IntPtr hWnd,
        IntPtr prcRect);
}

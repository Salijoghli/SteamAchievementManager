using System.ComponentModel;
using System.Runtime.InteropServices;
using SAM.App.Services;

namespace SAM.App.Infrastructure;

/// <summary>
/// The app's notification-area ("system tray") icon, via Shell_NotifyIcon. Left-click raises
/// <see cref="OpenRequested"/>; right-click shows a menu with "Open library" and "Exit".
/// </summary>
/// <remarks>
/// Create and use it on the UI thread: it owns a hidden window whose messages are pumped by that
/// thread's message loop. The icon is only added while <see cref="IsVisible"/> is true, and is
/// re-added if Explorer restarts. Dispose removes it, so no ghost icon is left behind.
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private const string DefaultToolTip = "Steam Achievement Manager";
    private const string WindowClassName = "SteamAchievementManager.TrayIcon";
    private const uint IconId = 1;
    private const uint CallbackMessage = WmApp + 1;
    private const int OpenCommand = 1;
    private const int ExitCommand = 2;

    private static readonly WndProc WindowProcedure = StaticWindowProc;
    private static readonly Dictionary<IntPtr, TrayIcon> Instances = [];
    private static readonly uint TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    private static ushort _windowClass;

    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private string _toolTip = DefaultToolTip;
    private bool _visible;
    private bool _added;
    private bool _disposed;

    /// <summary>Creates the (hidden) icon with the app's icon from Assets\SAM.ico. Throws Win32Exception on failure.</summary>
    public TrayIcon()
    {
        EnsureWindowClass();
        this._hwnd = CreateWindowEx(0, WindowClassName, DefaultToolTip, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (this._hwnd == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't create the tray icon's window.");
        }

        Instances[this._hwnd] = this;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "SAM.ico");
        this._icon = LoadImage(IntPtr.Zero, iconPath, ImageIcon, GetSystemMetrics(SmCxSmIcon), GetSystemMetrics(SmCySmIcon), LrLoadFromFile);
        if (this._icon == IntPtr.Zero)
        {
            Diagnostics.Log($"Couldn't load the tray icon from {iconPath} (error {Marshal.GetLastWin32Error()})");
        }
    }

    /// <summary>Left-click on the icon, or "Open library" in its menu. Raised on the UI thread.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>"Exit" in the icon's menu. Raised on the UI thread.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>
    /// Whether the icon is shown in the notification area. Stays false after setting it to true
    /// if the shell refused to add the icon.
    /// </summary>
    public bool IsVisible
    {
        get => this._visible;
        set
        {
            if (this._disposed || this._visible == value)
            {
                return;
            }

            this._visible = value;
            if (value)
            {
                this.Add();
            }
            else
            {
                this.Remove();
            }
        }
    }

    /// <summary>
    /// The text shown when hovering the icon, e.g. a background task's status. Null or empty
    /// restores the default ("Steam Achievement Manager"). Truncated to 127 characters.
    /// </summary>
    public string? ToolTip
    {
        get => this._toolTip;
        set
        {
            var text = string.IsNullOrEmpty(value) ? DefaultToolTip : value;
            this._toolTip = text.Length > 127 ? text[..127] : text;
            if (this._added)
            {
                var data = this.CreateData(NifTip);
                Shell_NotifyIcon(NimModify, ref data);
            }
        }
    }

    public void Dispose()
    {
        if (this._disposed)
        {
            return;
        }

        this.Remove();
        this._disposed = true;
        this._visible = false;
        Instances.Remove(this._hwnd);
        DestroyWindow(this._hwnd);
        if (this._icon != IntPtr.Zero)
        {
            DestroyIcon(this._icon);
        }
    }

    private void Add()
    {
        var data = this.CreateData(NifMessage | NifIcon | NifTip);
        this._added = Shell_NotifyIcon(NimAdd, ref data);
        if (this._added == false)
        {
            // IsVisible reports what's really there, so callers don't hide a window to a missing icon.
            this._visible = false;
            Diagnostics.Log("Couldn't add the tray icon");
        }
    }

    private void Remove()
    {
        if (this._added)
        {
            var data = this.CreateData(0);
            Shell_NotifyIcon(NimDelete, ref data);
            this._added = false;
        }
    }

    private NotifyIconData CreateData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = this._hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = this._icon,
        szTip = this._toolTip,
        szInfo = "",
        szInfoTitle = "",
    };

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MfString, OpenCommand, "Open library");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, ExitCommand, "Exit");
            SetMenuDefaultItem(menu, OpenCommand, 0);

            GetCursorPos(out var point);

            // Required so the menu closes when the user clicks elsewhere.
            SetForegroundWindow(this._hwnd);
            var command = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton | TpmNoNotify, point.X, point.Y, this._hwnd, IntPtr.Zero);
            PostMessage(this._hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case OpenCommand:
                    this.OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case ExitCommand:
                    this.ExitRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private IntPtr WindowProc(uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == CallbackMessage)
        {
            switch ((uint)lParam.ToInt64() & 0xFFFF)
            {
                case WmLButtonUp:
                    this.OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case WmRButtonUp:
                    this.ShowMenu();
                    break;
            }

            return IntPtr.Zero;
        }

        if (message == TaskbarCreatedMessage && TaskbarCreatedMessage != 0 && this._visible)
        {
            // Explorer restarted and lost every icon: add ours again.
            this._added = false;
            this.Add();
            return IntPtr.Zero;
        }

        return DefWindowProc(this._hwnd, message, wParam, lParam);
    }

    private static IntPtr StaticWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (Instances.TryGetValue(hwnd, out var instance))
        {
            try
            {
                return instance.WindowProc(message, wParam, lParam);
            }
            catch (Exception e)
            {
                // Exceptions can't cross back into native code.
                Diagnostics.Log("Tray icon message handling failed", e);
                return IntPtr.Zero;
            }
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static void EnsureWindowClass()
    {
        if (_windowClass != 0)
        {
            return;
        }

        var windowClass = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WindowProcedure),
            hInstance = GetModuleHandle(null),
            lpszClassName = WindowClassName,
        };
        _windowClass = RegisterClassEx(ref windowClass);
        if (_windowClass == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't register the tray icon's window class.");
        }
    }

    #region Win32
    private const uint WmNull = 0x0000;
    private const uint WmApp = 0x8000;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NifMessage = 0x1;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;
    private const uint MfString = 0x0;
    private const uint MfSeparator = 0x800;
    private const uint TpmRightButton = 0x2;
    private const uint TpmNoNotify = 0x80;
    private const uint TpmReturnCmd = 0x100;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x10;
    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
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
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegisterClassExW")]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LoadImageW")]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, nint id, string? text);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    #endregion
}

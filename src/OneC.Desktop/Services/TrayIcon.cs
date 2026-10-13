using System.Runtime.InteropServices;

namespace OneC.Desktop.Services;

/// <summary>
/// The notification-area icon: Shell_NotifyIcon on the main window's handle, its mouse messages
/// read through a window subclass. Left click shows the window, right click opens Show / Quit.
/// Plain Win32 on purpose — no extra package for one icon and two menu items.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint WmTray = 0x8000 + 0x41;       // WM_APP + n: our callback message
    private const uint WmLButtonUp = 0x0202, WmRButtonUp = 0x0205;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4, NifInfo = 0x10;
    private const uint CmdShow = 1, CmdQuit = 2;

    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private readonly string _tip;
    private readonly Action _show, _quit;
    private readonly SubclassProc _proc;              // kept alive: native code holds a pointer to it
    private readonly uint _taskbarCreated;
    private bool _added;

    public TrayIcon(IntPtr hwnd, string iconPath, string tip, Action show, Action quit)
    {
        _hwnd = hwnd;
        _tip = tip;
        _show = show;
        _quit = quit;
        _icon = LoadImage(IntPtr.Zero, iconPath, 1 /*IMAGE_ICON*/, GetSystemMetrics(49), GetSystemMetrics(50), 0x10 /*LR_LOADFROMFILE*/);
        // Explorer restarts drop every notification icon; this message says "add yours again".
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _proc = WndProc;
        SetWindowSubclass(_hwnd, _proc, 0x41494241, IntPtr.Zero);
        Add();
    }

    private void Add()
    {
        var d = Data(NifMessage | NifIcon | NifTip);
        _added = Shell_NotifyIcon(NimAdd, ref d);
    }

    /// <summary>A one-off balloon next to the icon (Windows shows it as a notification).</summary>
    public void Notify(string title, string text)
    {
        if (!_added) return;
        var d = Data(NifInfo);
        d.szInfoTitle = title;
        d.szInfo = text;
        Shell_NotifyIcon(NimModify, ref d);
    }

    private NotifyIconData Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WmTray,
        hIcon = _icon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        if (msg == WmTray)
        {
            uint ev = (uint)(lParam.ToInt64() & 0xFFFF);
            if (ev == WmLButtonUp) _show();
            else if (ev == WmRButtonUp) Menu();
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreated && _taskbarCreated != 0) Add();
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void Menu()
    {
        IntPtr menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, 0, CmdShow, "Open AIBA Connector");
            AppendMenu(menu, 0x800 /*MF_SEPARATOR*/, 0, null);
            AppendMenu(menu, 0, CmdQuit, "Quit (stops sync)");
            GetCursorPos(out var p);
            // Without this the menu does not close when the user clicks elsewhere.
            SetForegroundWindow(_hwnd);
            uint cmd = TrackPopupMenuEx(menu, 0x100 /*TPM_RETURNCMD*/ | 0x2 /*TPM_RIGHTBUTTON*/, p.X, p.Y, _hwnd, IntPtr.Zero);
            if (cmd == CmdShow) _show();
            else if (cmd == CmdQuit) _quit();
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_added)
        {
            var d = Data(0);
            Shell_NotifyIcon(NimDelete, ref d);
            _added = false;
        }
        RemoveWindowSubclass(_hwnd, _proc, 0x41494241);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint msg, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr inst, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id, IntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}

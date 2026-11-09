using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using OpenWrtDesk.Core;

namespace OpenWrtDesk;

public sealed class TrayIcon : IDisposable
{
    private const uint Callback = 0x8000 + 77;
    private readonly nint hwnd;
    private readonly DispatcherQueue dispatcher;
    private readonly Action show, exit;
    private readonly Action<Guid> select;
    private readonly Func<List<RouterProfile>> profiles;
    private readonly SubclassProc proc;
    private NotifyData data;
    private bool disposed;
    public TrayIcon(nint handle, DispatcherQueue queue, Action showWindow, Action exitApp, Action<Guid> choose, Func<List<RouterProfile>> getProfiles)
    {
        hwnd = handle; dispatcher = queue; show = showWindow; exit = exitApp; select = choose; profiles = getProfiles;
        proc = WindowProc; SetWindowSubclass(hwnd, proc, 77, 0);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        var icon = File.Exists(iconPath) ? LoadImage(0, iconPath, 1, 32, 32, 0x10) : LoadIcon(0, 32512);
        data = new NotifyData { cbSize = (uint)Marshal.SizeOf<NotifyData>(), hWnd = hwnd, uID = 77, uFlags = 1 | 2 | 4, uCallbackMessage = Callback, hIcon = icon, szTip = "OpenWrt Desk", szInfo = "", szInfoTitle = "" };
        if (!ShellNotify(0, ref data)) throw new InvalidOperationException("无法创建系统托盘图标");
    }
    public void SetTooltip(string text)
    {
        if (disposed) return; data.szTip = text.Length > 120 ? text[..120] : text; data.uFlags = 4; ShellNotify(1, ref data);
    }
    public void Notify(string title, string message)
    {
        if (disposed) return;
        data.szInfoTitle = title.Length > 60 ? title[..60] : title; data.szInfo = message.Length > 240 ? message[..240] : message;
        data.uFlags = 0x10; data.dwInfoFlags = 1; data.uTimeoutOrVersion = 10000; ShellNotify(1, ref data);
    }
    private nint WindowProc(nint window, uint message, nuint wParam, nint lParam, nuint subclass, nuint reference)
    {
        if (message == Callback)
        {
            var action = (uint)(long)lParam & 0xFFFF;
            if (action is 0x0203 or 0x0405) dispatcher.TryEnqueue(() => show());
            if (action == 0x0205) Menu();
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }
    private void Menu()
    {
        var menu = CreatePopupMenu(); var devices = profiles().OrderBy(p => p.Order).Take(50).ToArray();
        try
        {
            AppendMenu(menu, 0, 1, "打开 OpenWrt Desk"); AppendMenu(menu, 0x800, 0, "");
            for (var i = 0; i < devices.Length; i++) AppendMenu(menu, 0, (nuint)(100 + i), devices[i].Name.Replace("&", "&&"));
            AppendMenu(menu, 0x800, 0, ""); AppendMenu(menu, 0, 2, "退出");
            GetCursorPos(out var point); SetForegroundWindow(hwnd);
            var chosen = TrackPopupMenu(menu, 0x100 | 2, point.X, point.Y, 0, hwnd, 0);
            if (chosen == 1) dispatcher.TryEnqueue(() => show());
            else if (chosen == 2) dispatcher.TryEnqueue(() => exit());
            else if (chosen >= 100 && chosen < 100 + devices.Length) { var id = devices[chosen - 100].Id; dispatcher.TryEnqueue(() => select(id)); }
            PostMessage(hwnd, 0, 0, 0);
        }
        finally { DestroyMenu(menu); }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; ShellNotify(2, ref data); RemoveWindowSubclass(hwnd, proc, 77);
        if (data.hIcon != 0) DestroyIcon(data.hIcon);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyData
    {
        public uint cbSize; public nint hWnd; public uint uID, uFlags, uCallbackMessage; public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public nint hBalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    private delegate nint SubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam, nuint id, nuint reference);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint reference);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] private static extern bool ShellNotify(uint command, ref NotifyData data);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern nint LoadIcon(nint instance, int name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rectangle);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(nint hwnd, uint msg, nuint wParam, nint lParam);
}

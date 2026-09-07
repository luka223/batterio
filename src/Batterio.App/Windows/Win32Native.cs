using System.Runtime.InteropServices;

namespace Batterio.App.Windows;

internal static class Win32Native
{
    internal const uint WM_NULL = 0x0000;
    internal const uint MF_STRING = 0x0000;
    internal const uint TPM_RIGHTBUTTON = 0x0002;
    internal const uint TPM_NONOTIFY = 0x0080;
    internal const uint TPM_RETURNCMD = 0x0100;

    [DllImport("user32.dll")]
    internal static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool AppendMenu(nint menu, uint flags, nuint itemId, string text);

    [DllImport("user32.dll")]
    internal static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint owner, nint rect);

    [DllImport("user32.dll")]
    internal static extern bool DestroyMenu(nint menu);

    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_DESTROY = 0x0002;
    internal const uint WM_LBUTTONUP = 0x0202;
    internal const uint WM_LBUTTONDBLCLK = 0x0203;
    internal const uint WM_RBUTTONUP = 0x0205;
    internal const uint WM_CONTEXTMENU = 0x007B;
    internal const uint WM_POWERBROADCAST = 0x0218;
    internal const nuint PBT_APMRESUMEAUTOMATIC = 0x0012;
    internal const nuint PBT_APMRESUMESUSPEND = 0x0007;
    internal const uint WM_APP = 0x8000;
    internal const uint WM_USER = 0x0400;

    internal const uint WS_EX_TOOLWINDOW = 0x00000080;
    internal const uint WS_EX_NOACTIVATE = 0x08000000;
    internal const uint WS_POPUP = 0x80000000;
    internal const int SW_HIDE = 0;
    internal const int SW_SHOW = 5;
    internal const int SW_SHOWNOACTIVATE = 4;
    internal const uint NIM_ADD = 0x00000000;
    internal const uint NIM_DELETE = 0x00000002;
    internal const uint NIM_SETVERSION = 0x00000004;
    internal const uint NIF_MESSAGE = 0x00000001;
    internal const uint NIF_ICON = 0x00000002;
    internal const uint NIF_TIP = 0x00000004;
    internal const uint NIF_GUID = 0x00000020;
    internal const uint NOTIFYICON_VERSION_4 = 4;
    internal const uint NIN_SELECT = WM_USER;
    internal const uint NIN_KEYSELECT = WM_USER + 1;
    internal const uint MONITOR_DEFAULTTONEAREST = 2;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        public uint Size;
        public uint Style;
        public WindowProcedure WindowProcedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        public nint Window;
        public uint MessageId;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NotifyIconIdentifier
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public Guid GuidItem;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool UnregisterClass(string className, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll")]
    internal static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    internal static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    internal static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    internal static extern nint DispatchMessage(ref Message message);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromRect(ref Rect rect, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect iconLocation);
}


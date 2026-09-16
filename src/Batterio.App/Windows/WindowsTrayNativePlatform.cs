using System.ComponentModel;
using System.Runtime.InteropServices;
using Batterio.Core.Abstractions;
using Windows.Win32;

namespace Batterio.App.Windows;

internal sealed class WindowsDoubleClickTimeProvider : IDoubleClickTimeProvider
{
    public TimeSpan GetDoubleClickTime() => TimeSpan.FromMilliseconds(PInvoke.GetDoubleClickTime());
}

/// <summary>Win32 implementation for the tray service's native boundary.</summary>
internal sealed class WindowsTrayNativePlatform : ITrayNativePlatform
{
    private const nuint RetryTimerId = 1;
    private const uint TrayCallbackMessage = Win32Native.WM_APP + 1;
    private static readonly Guid TrayGuid = new("9D3B7E6E-5CB9-4A63-AE76-5CF12CDB8E4C");
    private readonly object _gate = new();
    private readonly Dictionary<string, Win32Native.WindowProcedure> _windowProcedures = new(StringComparer.Ordinal);

    public nint GetModuleHandle() => Win32Native.GetModuleHandle(null);

    public nint LoadIcon(string path)
    {
        var icon = Win32Native.LoadImage(0, path, 1, 0, 0, 0x10 | 0x40);
        if (icon == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not load the Batterio icon.");
        }

        return icon;
    }

    public uint RegisterTaskbarCreatedMessage()
    {
        var message = Win32Native.RegisterWindowMessage("TaskbarCreated");
        if (message == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not register the TaskbarCreated message.");
        }

        return message;
    }

    public ushort RegisterWindowClass(
        string className,
        nint instance,
        nint icon,
        uint taskbarCreatedMessage,
        TrayNativeWindowProcedure windowProcedure)
    {
        Win32Native.WindowProcedure nativeProcedure = (window, message, wParam, lParam) =>
        {
            if (message == taskbarCreatedMessage)
            {
                return windowProcedure(window, new(TrayWindowEventKind.TaskbarCreated, window, wParam, lParam));
            }

            if (message == Win32Native.WM_TIMER && wParam == RetryTimerId)
            {
                return windowProcedure(window, new(TrayWindowEventKind.RetryTimer, window, wParam, lParam));
            }

            if (message == Win32Native.WM_CLOSE)
            {
                return windowProcedure(window, new(TrayWindowEventKind.Close, window, wParam, lParam));
            }

            if (message == Win32Native.WM_DESTROY)
            {
                return windowProcedure(window, new(TrayWindowEventKind.Destroy, window, wParam, lParam));
            }

            if (message == Win32Native.WM_POWERBROADCAST &&
                (wParam == Win32Native.PBT_APMRESUMEAUTOMATIC || wParam == Win32Native.PBT_APMRESUMESUSPEND))
            {
                return windowProcedure(window, new(TrayWindowEventKind.SystemResumed, window, wParam, lParam));
            }

            if (message == TrayCallbackMessage)
            {
                var callbackKind = (uint)(lParam.ToInt64() & 0xffff) switch
                {
                    Win32Native.WM_LBUTTONUP => TrayWindowEventKind.LeftButtonUp,
                    Win32Native.WM_LBUTTONDBLCLK => TrayWindowEventKind.LeftButtonDoubleClick,
                    Win32Native.WM_RBUTTONUP => TrayWindowEventKind.RightButtonUp,
                    Win32Native.WM_CONTEXTMENU => TrayWindowEventKind.ContextMenu,
                    Win32Native.NIN_KEYSELECT => TrayWindowEventKind.KeyboardActivate,
                    _ => (TrayWindowEventKind?)null
                };
                if (callbackKind.HasValue)
                {
                    return windowProcedure(window, new(callbackKind.Value, window, wParam, lParam));
                }

                return 0;
            }

            return Win32Native.DefWindowProc(window, message, wParam, lParam);
        };

        var windowClass = new Win32Native.WindowClass
        {
            Size = (uint)Marshal.SizeOf<Win32Native.WindowClass>(),
            Instance = instance,
            WindowProcedure = nativeProcedure,
            ClassName = className,
            Icon = icon,
            SmallIcon = icon
        };

        var atom = Win32Native.RegisterClassEx(ref windowClass);
        if (atom == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not register the tray callback window class.");
        }

        lock (_gate)
        {
            _windowProcedures.Add(className, nativeProcedure);
        }

        return atom;
    }

    public nint CreateCallbackWindow(string className, nint instance)
    {
        var window = Win32Native.CreateWindowEx(
            Win32Native.WS_EX_TOOLWINDOW | Win32Native.WS_EX_NOACTIVATE,
            className,
            "Batterio tray callback",
            Win32Native.WS_POPUP,
            0,
            0,
            0,
            0,
            0,
            0,
            instance,
            0);
        if (window == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not create the tray callback window.");
        }

        return window;
    }

    public int GetMessage(out object message)
    {
        var result = Win32Native.GetMessage(out var nativeMessage, 0, 0, 0);
        message = nativeMessage;
        return result;
    }

    public bool TranslateMessage(object message)
    {
        var nativeMessage = (Win32Native.Message)message;
        return Win32Native.TranslateMessage(ref nativeMessage);
    }

    public nint DispatchMessage(object message)
    {
        var nativeMessage = (Win32Native.Message)message;
        return Win32Native.DispatchMessage(ref nativeMessage);
    }

    public bool TryAddIcon(nint window, nint icon, out bool version4)
    {
        var data = CreateNotifyIconData(window, icon);
        if (!Win32Native.Shell_NotifyIcon(Win32Native.NIM_ADD, ref data))
        {
            version4 = false;
            return false;
        }

        data.TimeoutOrVersion = Win32Native.NOTIFYICON_VERSION_4;
        version4 = Win32Native.Shell_NotifyIcon(Win32Native.NIM_SETVERSION, ref data);
        return true;
    }

    public bool DeleteIcon(nint window)
    {
        var data = CreateNotifyIconData(window, 0);
        return Win32Native.Shell_NotifyIcon(Win32Native.NIM_DELETE, ref data);
    }

    public nuint StartRetryTimer(nint window, nuint timerId, uint intervalMilliseconds) =>
        Win32Native.SetTimer(window, timerId, intervalMilliseconds, 0);

    public bool CancelRetryTimer(nint window, nuint timerId) => Win32Native.KillTimer(window, timerId);

    public bool PostClose(nint window) => Win32Native.PostMessage(window, Win32Native.WM_CLOSE, 0, 0);

    public void PostQuitMessage(int exitCode) => Win32Native.PostQuitMessage(exitCode);

    public bool DestroyWindow(nint window) => Win32Native.DestroyWindow(window);

    public bool UnregisterWindowClass(string className, nint instance)
    {
        var result = Win32Native.UnregisterClass(className, instance);
        if (result)
        {
            lock (_gate)
            {
                _windowProcedures.Remove(className);
            }
        }

        return result;
    }

    public bool DestroyIcon(nint icon) => Win32Native.DestroyIcon(icon);

    public TrayIconPlacement? GetIconPlacement(nint window)
    {
        var identifier = new Win32Native.NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<Win32Native.NotifyIconIdentifier>(),
            Window = window,
            Id = 1,
            GuidItem = TrayGuid
        };
        if (Win32Native.Shell_NotifyIconGetRect(ref identifier, out var rect) != 0)
        {
            return GetFallbackPlacement();
        }

        return CreatePlacement(rect);
    }

    private static Win32Native.NotifyIconData CreateNotifyIconData(nint window, nint icon) => new()
    {
        Size = (uint)Marshal.SizeOf<Win32Native.NotifyIconData>(),
        Window = window,
        Id = 1,
        Flags = Win32Native.NIF_MESSAGE | Win32Native.NIF_ICON | Win32Native.NIF_TIP | Win32Native.NIF_GUID,
        CallbackMessage = TrayCallbackMessage,
        Icon = icon,
        Tip = "Batterio",
        Info = string.Empty,
        InfoTitle = string.Empty,
        GuidItem = TrayGuid
    };

    private static TrayIconPlacement? CreatePlacement(Win32Native.Rect iconRect)
    {
        var monitor = Win32Native.MonitorFromRect(ref iconRect, Win32Native.MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new Win32Native.MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<Win32Native.MonitorInfo>()
        };
        if (monitor == 0 || !Win32Native.GetMonitorInfo(monitor, ref monitorInfo))
        {
            return null;
        }

        return new TrayIconPlacement(
            iconRect.Left,
            iconRect.Top,
            iconRect.Right,
            iconRect.Bottom,
            monitorInfo.Work.Left,
            monitorInfo.Work.Top,
            monitorInfo.Work.Right,
            monitorInfo.Work.Bottom);
    }

    private static TrayIconPlacement? GetFallbackPlacement()
    {
        if (!Win32Native.GetCursorPos(out var point))
        {
            return null;
        }

        var rect = new Win32Native.Rect
        {
            Left = point.X,
            Top = point.Y,
            Right = point.X + 1,
            Bottom = point.Y + 1
        };
        return CreatePlacement(rect);
    }
}

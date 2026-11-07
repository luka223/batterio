using System.Runtime.InteropServices;
using Batterio.Core.Abstractions;

namespace Batterio.App.Windows;

/// <summary>Where the taskbar strip may be placed: the taskbar window and the x of its free right edge.</summary>
/// <param name="Taskbar">The primary taskbar window (<c>Shell_TrayWnd</c>).</param>
/// <param name="ScreenLeft">Left edge of the taskbar in screen pixels.</param>
/// <param name="ScreenTop">Top edge of the taskbar in screen pixels.</param>
/// <param name="Width">Taskbar width in pixels.</param>
/// <param name="Height">Taskbar height in pixels.</param>
/// <param name="RightEdge">
/// X coordinate, in taskbar client pixels, that the strip's right edge should touch.
/// This is already moved left of the notification area by a small gap.
/// </param>
/// <param name="Dpi">DPI of the taskbar window.</param>
internal readonly record struct TaskbarAnchor(
    nint Taskbar,
    int ScreenLeft,
    int ScreenTop,
    int Width,
    int Height,
    int RightEdge,
    uint Dpi);

/// <summary>
/// Isolates the fragile "where is the system tray" logic.
/// <para>
/// Primary method (verified on Windows 11 build 26300): the <c>TrayNotifyWnd</c> child of
/// <c>Shell_TrayWnd</c> still reports a correct rectangle spanning the tray icons, the
/// overflow chevron and the clock, so its left edge is the anchor. If a future Windows
/// update reports an empty or out-of-bounds rectangle, the left edge of Batterio's own tray
/// icon (<c>Shell_NotifyIconGetRect</c>, supplied as <c>fallback</c>) is used instead.
/// </para>
/// </summary>
internal static class TaskbarLocator
{
    private const string TaskbarClass = "Shell_TrayWnd";
    private const string NotifyAreaClass = "TrayNotifyWnd";

    /// <summary>Space kept between the strip and the notification area, in 96-DPI pixels.</summary>
    internal const int GapAtDefaultDpi = 4;

    public static bool TryLocate(Func<TrayIconPlacement?>? fallback, out TaskbarAnchor anchor)
    {
        anchor = default;
        var taskbar = Win32Native.FindWindow(TaskbarClass, null);
        if (taskbar == 0 || !Win32Native.GetWindowRect(taskbar, out var taskbarRect))
        {
            return false;
        }

        var width = taskbarRect.Right - taskbarRect.Left;
        var height = taskbarRect.Bottom - taskbarRect.Top;
        if (width <= 0 || height <= 0 || width <= height || !IsBottomDocked(taskbarRect))
        {
            return false;
        }

        var dpi = Win32Native.GetDpiForWindow(taskbar);
        if (dpi == 0)
        {
            dpi = 96;
        }

        var gap = (int)Math.Round(GapAtDefaultDpi * dpi / 96d);
        var edge = FindNotifyAreaLeft(taskbar, taskbarRect) ?? FindFallbackLeft(fallback, taskbarRect);
        if (edge is null)
        {
            return false;
        }

        anchor = new TaskbarAnchor(
            taskbar,
            taskbarRect.Left,
            taskbarRect.Top,
            width,
            height,
            edge.Value - taskbarRect.Left - gap,
            dpi);
        return true;
    }

    private static int? FindNotifyAreaLeft(nint taskbar, Win32Native.Rect taskbarRect)
    {
        var notify = Win32Native.FindWindowEx(taskbar, 0, NotifyAreaClass, null);
        if (notify == 0 || !Win32Native.GetWindowRect(notify, out var rect))
        {
            return null;
        }

        return IsUsableEdge(rect.Left, rect.Right - rect.Left, taskbarRect) ? rect.Left : null;
    }

    private static int? FindFallbackLeft(Func<TrayIconPlacement?>? fallback, Win32Native.Rect taskbarRect)
    {
        TrayIconPlacement? placement;
        try
        {
            placement = fallback?.Invoke();
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            return null;
        }

        return placement is { } icon && IsUsableEdge(icon.IconLeft, icon.IconRight - icon.IconLeft, taskbarRect)
            ? icon.IconLeft
            : null;
    }

    private static bool IsUsableEdge(int left, int width, Win32Native.Rect taskbarRect) =>
        width > 0 && left > taskbarRect.Left && left < taskbarRect.Right;

    private static bool IsBottomDocked(Win32Native.Rect taskbarRect)
    {
        var rect = taskbarRect;
        var monitor = Win32Native.MonitorFromRect(ref rect, Win32Native.MONITOR_DEFAULTTONEAREST);
        var info = new Win32Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Win32Native.MonitorInfo>() };
        if (monitor == 0 || !Win32Native.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var monitorHeight = info.Monitor.Bottom - info.Monitor.Top;
        return taskbarRect.Bottom >= info.Monitor.Bottom && taskbarRect.Top > info.Monitor.Top + monitorHeight / 2;
    }
}

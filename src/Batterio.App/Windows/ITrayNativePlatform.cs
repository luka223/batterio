using Batterio.Core.Abstractions;

namespace Batterio.App.Windows;

internal enum TrayWindowEventKind
{
    TaskbarCreated,
    RetryTimer,
    Close,
    Destroy,
    LeftButtonUp,
    LeftButtonDoubleClick,
    RightButtonUp,
    ContextMenu,
    KeyboardActivate,
    SystemResumed
}

internal readonly record struct TrayWindowEvent(
    TrayWindowEventKind Kind,
    nint Window = 0,
    nuint WParam = 0,
    nint LParam = 0);

internal delegate nint TrayNativeWindowProcedure(nint window, TrayWindowEvent message);

/// <summary>Native operations used by tray startup, message handling, retry and teardown.</summary>
internal interface ITrayNativePlatform
{
    nint GetModuleHandle();

    nint LoadIcon(string path);

    uint RegisterTaskbarCreatedMessage();

    ushort RegisterWindowClass(
        string className,
        nint instance,
        nint icon,
        uint taskbarCreatedMessage,
        TrayNativeWindowProcedure windowProcedure);

    nint CreateCallbackWindow(string className, nint instance);

    int GetMessage(out object message);

    bool TranslateMessage(object message);

    nint DispatchMessage(object message);

    bool TryAddIcon(nint window, nint icon, out bool version4);

    bool DeleteIcon(nint window);

    nuint StartRetryTimer(nint window, nuint timerId, uint intervalMilliseconds);

    bool CancelRetryTimer(nint window, nuint timerId);

    bool PostClose(nint window);

    void PostQuitMessage(int exitCode);

    bool DestroyWindow(nint window);

    bool UnregisterWindowClass(string className, nint instance);

    bool DestroyIcon(nint icon);

    TrayIconPlacement? GetIconPlacement(nint window);
}

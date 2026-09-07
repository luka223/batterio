using Batterio.Core;
using Batterio.Core.Abstractions;
using Windows.Win32;
using System.Runtime.InteropServices;

namespace Batterio.App.Windows;

public sealed class WindowsDoubleClickTimeProvider : IDoubleClickTimeProvider
{
    public TimeSpan GetDoubleClickTime() => TimeSpan.FromMilliseconds(PInvoke.GetDoubleClickTime());
}

public sealed class TrayService : ITrayService
{
    private static readonly Guid TrayGuid = new("9D3B7E6E-5CB9-4A63-AE76-5CF12CDB8E4C");
    private readonly IBatterioLog _log;
    private readonly TrayClickArbiter _clickArbiter;
    private readonly TaskCompletionSource<bool> _loopCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly Win32Native.WindowProcedure _windowProcedure;
    private Thread? _thread;
    private nint _window;
    private nint _instance;
    private nint _icon;
    private ushort _classAtom;
    private uint _taskbarCreatedMessage;
    private bool _iconAdded;
    private bool _version4;
    private bool _started;

    public TrayService(IBatterioLog log)
    {
        _log = log;
        _clickArbiter = new TrayClickArbiter(new SystemClickDelay(), new WindowsDoubleClickTimeProvider());
        _clickArbiter.ActionRaised += (_, action) => ActionRaised?.Invoke(this, action);
        _windowProcedure = WindowProcedure;
    }

    public event EventHandler<TrayAction>? ActionRaised;

    public event EventHandler? SystemResumed;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _thread = new Thread(MessageLoop)
            {
                IsBackground = true,
                Name = "Batterio tray window"
            };
            _thread.Start();
        }

        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        nint window;
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            window = _window;
        }

        if (window != 0)
        {
            Win32Native.PostMessage(window, Win32Native.WM_CLOSE, 0, 0);
        }

        try
        {
            await _loopCompleted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
        {
            _log.Log(BatterioLogLevel.Warning, "Tray service stopped with a prior initialization failure.", exception);
        }
    }

    public TrayIconPlacement? GetIconPlacement()
    {
        nint window;
        lock (_gate)
        {
            window = _window;
        }

        if (window == 0 || !_iconAdded)
        {
            return null;
        }

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

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _clickArbiter.DisposeAsync().ConfigureAwait(false);
    }

    private void MessageLoop()
    {
        try
        {
            _instance = Win32Native.GetModuleHandle(null);
            _icon = Win32Native.LoadImage(0, Path.Combine(AppContext.BaseDirectory, "batterio.ico"), 1, 0, 0, 0x10 | 0x40);
            if (_icon == 0)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not load the Batterio icon.");
            }
            var className = $"Batterio.Tray.{Environment.ProcessId}";
            var windowClass = new Win32Native.WindowClass
            {
                Size = (uint)Marshal.SizeOf<Win32Native.WindowClass>(),
                Instance = _instance,
                WindowProcedure = _windowProcedure,
                ClassName = className,
                Icon = _icon,
                SmallIcon = _icon
            };
            _classAtom = Win32Native.RegisterClassEx(ref windowClass);
            if (_classAtom == 0)
            {
                throw new InvalidOperationException("Windows could not register the tray callback window class.");
            }

            _taskbarCreatedMessage = Win32Native.RegisterWindowMessage("TaskbarCreated");
            _window = Win32Native.CreateWindowEx(
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
                _instance,
                0);
            if (_window == 0)
            {
                throw new InvalidOperationException("Windows could not create the tray callback window.");
            }

            if (!AddIcon())
            {
                throw new InvalidOperationException("Windows could not register the Batterio tray icon.");
            }

            _ready.TrySetResult(true);
            while (Win32Native.GetMessage(out var message, 0, 0, 0) > 0)
            {
                Win32Native.TranslateMessage(ref message);
                Win32Native.DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Critical, "Tray service failed to initialize.", exception);
            _ready.TrySetException(exception);
            _loopCompleted.TrySetException(exception);
        }
        finally
        {
            if (_classAtom != 0)
            {
                Win32Native.UnregisterClass($"Batterio.Tray.{Environment.ProcessId}", _instance);
            }

            lock (_gate)
            {
                _window = 0;
            }

            if (_icon != 0)
            {
                Win32Native.DestroyIcon(_icon);
                _icon = 0;
            }

            _loopCompleted.TrySetResult(true);
        }
    }

    private nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == _taskbarCreatedMessage)
        {
            RestoreIcon();
            return 0;
        }

        if (message == TrayCallbackMessage)
        {
            // Version 4 packs the icon ID into the high word of lParam.
            switch ((uint)(lParam.ToInt64() & 0xffff))
            {
                case Win32Native.WM_LBUTTONUP:
                    _ = _clickArbiter.HandleAsync(TrayNotification.LeftButtonUp);
                    return 0;
                case Win32Native.WM_LBUTTONDBLCLK:
                    _ = _clickArbiter.HandleAsync(TrayNotification.LeftButtonDoubleClick);
                    return 0;
                case Win32Native.WM_RBUTTONUP:
                    if (_version4)
                    {
                        // Version 4 sends WM_CONTEXTMENU for this same gesture.
                        return 0;
                    }
                    _ = _clickArbiter.HandleAsync(TrayNotification.RightButtonUp);
                    return 0;
                case Win32Native.WM_CONTEXTMENU:
                    _ = _clickArbiter.HandleAsync(TrayNotification.RightButtonUp);
                    return 0;
                case Win32Native.NIN_SELECT:
                    // Mouse activation also sends WM_LBUTTONUP; arbitrate it only once.
                    return 0;
                case Win32Native.NIN_KEYSELECT:
                    _ = _clickArbiter.HandleAsync(TrayNotification.KeyboardActivate);
                    return 0;
            }
        }

        if (message == Win32Native.WM_POWERBROADCAST &&
            (wParam == Win32Native.PBT_APMRESUMEAUTOMATIC || wParam == Win32Native.PBT_APMRESUMESUSPEND))
        {
            SystemResumed?.Invoke(this, EventArgs.Empty);
            return 1;
        }

        if (message == Win32Native.WM_CLOSE)
        {
            Win32Native.DestroyWindow(window);
            return 0;
        }

        if (message == Win32Native.WM_DESTROY)
        {
            RemoveIcon();
            Win32Native.PostQuitMessage(0);
            return 0;
        }

        return Win32Native.DefWindowProc(window, message, wParam, lParam);
    }

    private const uint TrayCallbackMessage = Win32Native.WM_APP + 1;

    private bool AddIcon()
    {
        var data = CreateNotifyIconData();
        if (!Win32Native.Shell_NotifyIcon(Win32Native.NIM_ADD, ref data))
        {
            return false;
        }

        data.TimeoutOrVersion = Win32Native.NOTIFYICON_VERSION_4;
        _version4 = Win32Native.Shell_NotifyIcon(Win32Native.NIM_SETVERSION, ref data);
        if (!_version4)
        {
            _log.Log(BatterioLogLevel.Warning, "Tray icon was added but notification version 4 could not be selected.");
        }

        _iconAdded = true;
        return true;
    }

    private void RestoreIcon()
    {
        if (_iconAdded)
        {
            RemoveIcon();
        }

        if (!AddIcon())
        {
            _log.Log(BatterioLogLevel.Error, "Explorer restarted, but the tray icon could not be restored.");
        }
    }

    private void RemoveIcon()
    {
        if (!_iconAdded || _window == 0)
        {
            return;
        }

        var data = CreateNotifyIconData();
        Win32Native.Shell_NotifyIcon(Win32Native.NIM_DELETE, ref data);
        _iconAdded = false;
    }

    private Win32Native.NotifyIconData CreateNotifyIconData() => new()
    {
        Size = (uint)Marshal.SizeOf<Win32Native.NotifyIconData>(),
        Window = _window,
        Id = 1,
        Flags = Win32Native.NIF_MESSAGE | Win32Native.NIF_ICON | Win32Native.NIF_TIP | Win32Native.NIF_GUID,
        CallbackMessage = TrayCallbackMessage,
        Icon = _icon,
        Tip = "Batterio",
        Info = string.Empty,
        InfoTitle = string.Empty,
        GuidItem = TrayGuid
    };

    private TrayIconPlacement? CreatePlacement(Win32Native.Rect iconRect)
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

    private TrayIconPlacement? GetFallbackPlacement()
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


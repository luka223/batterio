using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Batterio.Core;
using Batterio.Core.Abstractions;

namespace Batterio.App.Windows;

/// <summary>Draws a battery strip next to the system tray and reports clicks on it.</summary>
public interface ITaskbarStripService : IAsyncDisposable
{
    /// <summary>Raised on the strip's own thread: <see cref="TrayAction.TogglePopup"/> or <see cref="TrayAction.ShowMenu"/>.</summary>
    event EventHandler<TrayAction>? ActionRaised;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces the drawn items. Safe to call from any thread and before <see cref="StartAsync"/>.</summary>
    void Update(IReadOnlyList<TaskbarStripItem> items);
}

/// <summary>
/// Hosts a layered, click-receiving window as a child of <c>Shell_TrayWnd</c>.
/// <para>
/// Two windows share one message loop. A hidden top-level <em>controller</em> owns the timer and
/// receives system broadcasts (<c>TaskbarCreated</c>, theme, DPI and display changes, which child
/// windows never get). The <em>strip</em> is the visible layered child. Explorer destroys the strip
/// when the taskbar is torn down (for example after a crash), so the strip is recreated on demand.
/// </para>
/// </summary>
public sealed class TaskbarStripService : ITaskbarStripService
{
    private const nuint TickTimerId = 2;
    private const uint TickIntervalMilliseconds = 1500;
    private const uint UpdateMessage = Win32Native.WM_APP + 2;

    private readonly IBatterioLog _log;
    private readonly Func<TrayIconPlacement?>? _fallbackPlacement;
    private readonly object _gate = new();
    private IReadOnlyList<TaskbarStripItem> _items = [];
    private StripRun? _run;
    private int _updatePending;
    private int _disposeRequested;

    public TaskbarStripService(IBatterioLog log, ITrayService tray)
        : this(log, CreateFallback(tray))
    {
    }

    private static Func<TrayIconPlacement?> CreateFallback(ITrayService tray)
    {
        ArgumentNullException.ThrowIfNull(tray);
        return tray.GetIconPlacement;
    }

    internal TaskbarStripService(IBatterioLog log, Func<TrayIconPlacement?>? fallbackPlacement)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _fallbackPlacement = fallbackPlacement;
    }

    public event EventHandler<TrayAction>? ActionRaised;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        Task readiness;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);
            if (_run is { } existing)
            {
                readiness = existing.Ready.Task;
            }
            else
            {
                var run = new StripRun
                {
                    ClassName = $"Batterio.TaskbarStrip.{Environment.ProcessId}.{Guid.NewGuid():N}"
                };
                run.Thread = new Thread(() => MessageLoop(run))
                {
                    IsBackground = true,
                    Name = "Batterio taskbar strip"
                };
                _run = run;
                try
                {
                    run.Thread.Start();
                }
                catch (Exception exception)
                {
                    _run = null;
                    run.Ready.TrySetException(exception);
                    run.Completion.TrySetResult(true);
                }

                readiness = run.Ready.Task;
            }
        }

        return cancellationToken.CanBeCanceled ? readiness.WaitAsync(cancellationToken) : readiness;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        StripRun? run;
        lock (_gate)
        {
            run = _run;
            if (run is null)
            {
                return;
            }

            run.RequestStop();
        }

        var controller = run.Controller;
        if (controller != 0 && !Win32Native.PostMessage(controller, Win32Native.WM_CLOSE, 0, 0))
        {
            _log.Log(BatterioLogLevel.Warning, "Windows did not accept the taskbar strip shutdown message.");
        }

        await run.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Update(IReadOnlyList<TaskbarStripItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        StripRun? run;
        lock (_gate)
        {
            _items = items.ToArray();
            run = _run;
        }

        var controller = run?.Controller ?? 0;
        if (controller == 0 || Interlocked.Exchange(ref _updatePending, 1) != 0)
        {
            return;
        }

        if (!Win32Native.PostMessage(controller, UpdateMessage, 0, 0))
        {
            Volatile.Write(ref _updatePending, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) == 0)
        {
            try
            {
                await StopAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _log.Log(BatterioLogLevel.Warning, "Taskbar strip disposal encountered an error.", exception);
            }
        }
    }

    private void MessageLoop(StripRun run)
    {
        try
        {
            run.Instance = Win32Native.GetModuleHandle(null);
            run.TaskbarCreatedMessage = Win32Native.RegisterWindowMessage("TaskbarCreated");
            run.WindowProcedure = (window, message, wParam, lParam) =>
                HandleMessage(run, window, message, wParam, lParam);

            var windowClass = new Win32Native.WindowClass
            {
                Size = (uint)Marshal.SizeOf<Win32Native.WindowClass>(),
                Instance = run.Instance,
                WindowProcedure = run.WindowProcedure,
                ClassName = run.ClassName,
                Cursor = Win32Native.LoadCursor(0, Win32Native.IDC_ARROW)
            };
            run.ClassAtom = Win32Native.RegisterClassEx(ref windowClass);
            if (run.ClassAtom == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not register the taskbar strip window class.");
            }

            var controller = Win32Native.CreateWindowEx(
                Win32Native.WS_EX_TOOLWINDOW | Win32Native.WS_EX_NOACTIVATE,
                run.ClassName,
                "Batterio taskbar strip controller",
                Win32Native.WS_POPUP,
                0, 0, 0, 0, 0, 0, run.Instance, 0);
            if (controller == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not create the taskbar strip controller window.");
            }

            run.Controller = controller;
            if (Win32Native.SetTimer(controller, TickTimerId, TickIntervalMilliseconds, 0) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not create the taskbar strip timer.");
            }

            run.Ready.TrySetResult(true);
            if (run.StopRequested)
            {
                Win32Native.PostMessage(controller, Win32Native.WM_CLOSE, 0, 0);
            }
            else
            {
                Refresh(run);
            }

            while (true)
            {
                var result = Win32Native.GetMessage(out var message, 0, 0, 0);
                if (result == -1)
                {
                    throw new InvalidOperationException("The taskbar strip message loop could not retrieve a Windows message.");
                }

                if (result == 0)
                {
                    break;
                }

                Win32Native.TranslateMessage(ref message);
                Win32Native.DispatchMessage(ref message);
                if (run.FatalException is { } fatal)
                {
                    throw fatal;
                }
            }
        }
        catch (Exception exception)
        {
            run.Failure = run.FatalException ?? exception;
            _log.Log(BatterioLogLevel.Error, "The taskbar strip failed and has been disabled.", run.Failure);
        }
        finally
        {
            CleanupNativeResources(run);

            if (!run.Ready.Task.IsCompleted)
            {
                run.Ready.TrySetException(run.Failure ??
                    new InvalidOperationException("The taskbar strip stopped before it became ready."));
            }

            run.Completion.TrySetResult(true);
            lock (_gate)
            {
                if (ReferenceEquals(_run, run))
                {
                    _run = null;
                }
            }
        }
    }

    private void CleanupNativeResources(StripRun run)
    {
        TryCleanup(() =>
        {
            if (run.Controller != 0)
            {
                Win32Native.KillTimer(run.Controller, TickTimerId);
            }
        }, "Could not cancel the taskbar strip timer.");
        TryCleanup(() => DestroyStrip(run), "Could not destroy the taskbar strip window.");
        TryCleanup(() =>
        {
            var controller = run.Controller;
            run.Controller = 0;
            if (controller != 0 && Win32Native.IsWindow(controller))
            {
                Win32Native.DestroyWindow(controller);
            }
        }, "Could not destroy the taskbar strip controller window.");
        TryCleanup(() =>
        {
            if (run.ClassAtom != 0)
            {
                Win32Native.UnregisterClass(run.ClassName, run.Instance);
                run.ClassAtom = 0;
            }
        }, "Could not unregister the taskbar strip window class.");
    }

    private void TryCleanup(Action cleanup, string message)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Warning, message, exception);
        }
    }

    private nint HandleMessage(StripRun run, nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (run.TaskbarCreatedMessage != 0 && message == run.TaskbarCreatedMessage)
            {
                // Explorer restarted: the old parent is gone and with it our strip.
                DestroyStrip(run);
                Refresh(run);
                return 0;
            }

            switch (message)
            {
                case UpdateMessage:
                    Volatile.Write(ref _updatePending, 0);
                    Refresh(run);
                    return 0;
                case Win32Native.WM_TIMER when wParam == TickTimerId:
                    Refresh(run);
                    return 0;
                case Win32Native.WM_SETTINGCHANGE:
                case Win32Native.WM_DISPLAYCHANGE:
                case Win32Native.WM_DPICHANGED:
                case Win32Native.WM_THEMECHANGED:
                    Refresh(run);
                    return Win32Native.DefWindowProc(window, message, wParam, lParam);
                case Win32Native.WM_MOUSEACTIVATE when window == run.Strip:
                    return Win32Native.MA_NOACTIVATE;
                case Win32Native.WM_LBUTTONUP when window == run.Strip:
                    RaiseAction(TrayAction.TogglePopup);
                    return 0;
                case Win32Native.WM_RBUTTONUP when window == run.Strip:
                    RaiseAction(TrayAction.ShowMenu);
                    return 0;
                case Win32Native.WM_CLOSE when window == run.Controller:
                    run.RequestStop();
                    Win32Native.DestroyWindow(window);
                    return 0;
                case Win32Native.WM_DESTROY:
                    if (window == run.Controller)
                    {
                        Win32Native.PostQuitMessage(0);
                    }
                    else if (window == run.Strip)
                    {
                        // Destroyed from outside (explorer tore its taskbar down). It is rebuilt on the next refresh.
                        run.ForgetStrip();
                    }

                    return 0;
                default:
                    return Win32Native.DefWindowProc(window, message, wParam, lParam);
            }
        }
        catch (Exception exception)
        {
            run.FatalException ??= exception;
            try
            {
                Win32Native.PostQuitMessage(0);
            }
            catch
            {
                // The loop observes the fatal exception on its next iteration.
            }

            return 0;
        }
    }

    private void RaiseAction(TrayAction action)
    {
        try
        {
            ActionRaised?.Invoke(this, action);
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Error, "A taskbar strip click handler failed.", exception);
        }
    }

    private IReadOnlyList<TaskbarStripItem> CurrentItems()
    {
        lock (_gate)
        {
            return _items;
        }
    }

    /// <summary>
    /// Brings the strip in line with the current items, theme, DPI and tray position. Cheap when
    /// nothing changed, so it also runs on the timer. Failures are logged and retried on the next tick.
    /// </summary>
    private void Refresh(StripRun run)
    {
        try
        {
            RefreshCore(run);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Log each distinct failure once; the timer retries every 1.5 s and would flood the log.
            var signature = $"{exception.GetType().Name}:{exception.Message}";
            if (!string.Equals(run.LastRefreshError, signature, StringComparison.Ordinal))
            {
                run.LastRefreshError = signature;
                _log.Log(BatterioLogLevel.Warning, "Updating the taskbar strip failed; it will be retried.", exception);
            }

            HideStrip(run);
            run.Rendered = null;
        }
    }

    private void RefreshCore(StripRun run)
    {
        var items = CurrentItems();
        if (items.Count == 0)
        {
            HideStrip(run);
            run.LastRefreshError = null;
            return;
        }

        if (!TaskbarLocator.TryLocate(_fallbackPlacement, out var anchor))
        {
            HideStrip(run);
            return;
        }

        var strip = EnsureStrip(run, anchor.Taskbar);
        var scale = anchor.Dpi / 96d;
        var light = TaskbarStripRenderer.IsLightTaskbar();
        var key = new RenderKey(items, scale, anchor.Height, light);
        var width = run.Width;
        if (run.Rendered is null || !run.Rendered.Equals(key))
        {
            using var bitmap = TaskbarStripRenderer.Render(items, scale, anchor.Height, light);
            if (bitmap is null)
            {
                HideStrip(run);
                return;
            }

            PushBitmap(strip, bitmap);
            width = bitmap.Width;
            run.Width = width;
            run.Rendered = key;
        }

        var x = Math.Max(0, anchor.RightEdge - width);
        var needsPlacement = !run.Visible || run.X != x || run.PlacedWidth != width || run.PlacedHeight != anchor.Height;
        if (needsPlacement)
        {
            if (!Win32Native.SetWindowPos(
                    strip,
                    Win32Native.HWND_TOP,
                    x,
                    0,
                    width,
                    anchor.Height,
                    Win32Native.SWP_NOACTIVATE | Win32Native.SWP_SHOWWINDOW))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not position the taskbar strip.");
            }

            run.X = x;
            run.PlacedWidth = width;
            run.PlacedHeight = anchor.Height;
            run.Visible = true;
        }

        run.LastRefreshError = null;
    }

    private nint EnsureStrip(StripRun run, nint taskbar)
    {
        if (run.Strip != 0 && (!Win32Native.IsWindow(run.Strip) || Win32Native.GetParent(run.Strip) != taskbar))
        {
            DestroyStrip(run);
        }

        if (run.Strip != 0)
        {
            return run.Strip;
        }

        var strip = Win32Native.CreateWindowEx(
            Win32Native.WS_EX_LAYERED | Win32Native.WS_EX_TOOLWINDOW | Win32Native.WS_EX_NOACTIVATE,
            run.ClassName,
            "Batterio taskbar strip",
            Win32Native.WS_POPUP,
            0, 0, 0, 0, 0, 0, run.Instance, 0);
        if (strip == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not create the taskbar strip window.");
        }

        run.Strip = strip;
        run.Rendered = null;
        run.Visible = false;

        // Switch to a child window before reparenting, as SetParent does not change WS_POPUP/WS_CHILD itself.
        var style = unchecked((uint)(long)Win32Native.GetWindowLongPtr(strip, Win32Native.GWL_STYLE));
        style = (style & ~Win32Native.WS_POPUP) | Win32Native.WS_CHILD;
        Win32Native.SetWindowLongPtr(strip, Win32Native.GWL_STYLE, unchecked((nint)(int)style));
        Win32Native.SetParent(strip, taskbar);
        if (Win32Native.GetParent(strip) != taskbar)
        {
            var error = Marshal.GetLastWin32Error();
            DestroyStrip(run);
            throw new Win32Exception(error, "Windows could not attach the strip to the taskbar.");
        }

        return strip;
    }

    private static void PushBitmap(nint strip, System.Drawing.Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var screenDc = Win32Native.GetDC(0);
        var memoryDc = Win32Native.CreateCompatibleDC(screenDc);
        nint section = 0;
        nint previous = 0;
        try
        {
            if (screenDc == 0 || memoryDc == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not create a device context for the taskbar strip.");
            }

            var header = new Win32Native.BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<Win32Native.BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = Win32Native.BI_RGB
            };
            section = Win32Native.CreateDIBSection(memoryDc, ref header, Win32Native.DIB_RGB_COLORS, out var bits, 0, 0);
            if (section == 0 || bits == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not create the taskbar strip surface.");
            }

            CopyPremultipliedPixels(bitmap, bits);
            previous = Win32Native.SelectObject(memoryDc, section);

            var size = new Win32Native.Size { Width = width, Height = height };
            var source = new Win32Native.Point();
            var blend = new Win32Native.BlendFunction
            {
                BlendOp = Win32Native.AC_SRC_OVER,
                SourceConstantAlpha = 255,
                AlphaFormat = Win32Native.AC_SRC_ALPHA
            };
            if (!Win32Native.UpdateLayeredWindow(strip, screenDc, 0, ref size, memoryDc, ref source, 0, ref blend, Win32Native.ULW_ALPHA))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not update the taskbar strip.");
            }
        }
        finally
        {
            if (previous != 0)
            {
                Win32Native.SelectObject(memoryDc, previous);
            }

            if (section != 0)
            {
                Win32Native.DeleteObject(section);
            }

            if (memoryDc != 0)
            {
                Win32Native.DeleteDC(memoryDc);
            }

            if (screenDc != 0)
            {
                Win32Native.ReleaseDC(0, screenDc);
            }
        }
    }

    private static void CopyPremultipliedPixels(System.Drawing.Bitmap bitmap, nint destination)
    {
        var rectangle = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var rowBytes = bitmap.Width * 4;
            for (var row = 0; row < bitmap.Height; row++)
            {
                unsafe
                {
                    Buffer.MemoryCopy(
                        (byte*)data.Scan0 + (long)row * data.Stride,
                        (byte*)destination + (long)row * rowBytes,
                        rowBytes,
                        rowBytes);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void HideStrip(StripRun run)
    {
        if (run.Strip != 0 && run.Visible)
        {
            Win32Native.ShowWindow(run.Strip, Win32Native.SW_HIDE);
        }

        run.Visible = false;
    }

    private static void DestroyStrip(StripRun run)
    {
        var strip = run.Strip;
        run.ForgetStrip();
        if (strip != 0 && Win32Native.IsWindow(strip))
        {
            Win32Native.DestroyWindow(strip);
        }
    }

    private sealed record RenderKey(IReadOnlyList<TaskbarStripItem> Items, double Scale, int Height, bool Light)
    {
        public bool Equals(RenderKey? other) =>
            other is not null &&
            Scale == other.Scale &&
            Height == other.Height &&
            Light == other.Light &&
            Items.SequenceEqual(other.Items);

        public override int GetHashCode() => HashCode.Combine(Items.Count, Scale, Height, Light);
    }

    private sealed class StripRun
    {
        private int _stopRequested;

        public readonly TaskCompletionSource<bool> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Thread Thread = null!;
        public string ClassName = string.Empty;
        public Win32Native.WindowProcedure? WindowProcedure;
        public nint Instance;
        public ushort ClassAtom;
        public uint TaskbarCreatedMessage;
        public volatile nint Controller;
        public nint Strip;
        public Exception? Failure;
        public Exception? FatalException;

        // Layout state, touched only on the strip thread.
        public RenderKey? Rendered;
        public bool Visible;
        public int Width;
        public int X;
        public int PlacedWidth;
        public int PlacedHeight;
        public string? LastRefreshError;

        public bool StopRequested => Volatile.Read(ref _stopRequested) != 0;

        public void RequestStop() => Interlocked.Exchange(ref _stopRequested, 1);

        public void ForgetStrip()
        {
            Strip = 0;
            Rendered = null;
            Visible = false;
            Width = 0;
            PlacedWidth = 0;
            PlacedHeight = 0;
        }
    }
}

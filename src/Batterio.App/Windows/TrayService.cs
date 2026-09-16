using Batterio.Core;
using Batterio.Core.Abstractions;

namespace Batterio.App.Windows;

internal enum TrayServiceLifecycleState
{
    Idle,
    Starting,
    Ready,
    Stopping,
    Faulted,
    Disposed
}

public sealed class TrayService : ITrayService
{
    private const nuint RetryTimerId = 1;
    private readonly IBatterioLog _log;
    private readonly TrayClickArbiter _clickArbiter;
    private readonly ITrayNativePlatform _platform;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly TaskCompletionSource<bool> _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TrayRunState? _currentRun;
    private TrayServiceLifecycleState _state = TrayServiceLifecycleState.Idle;
    private int _disposeRequested;

    public TrayService(IBatterioLog log)
        : this(
            log,
            new WindowsTrayNativePlatform(),
            TimeProvider.System,
            new SystemClickDelay(),
            new WindowsDoubleClickTimeProvider())
    {
    }

    internal TrayService(
        IBatterioLog log,
        ITrayNativePlatform platform,
        TimeProvider? timeProvider = null,
        IClickDelay? clickDelay = null,
        IDoubleClickTimeProvider? doubleClickTimeProvider = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _clickArbiter = new TrayClickArbiter(
            clickDelay ?? new SystemClickDelay(),
            doubleClickTimeProvider ?? new WindowsDoubleClickTimeProvider());
        _clickArbiter.ActionRaised += (_, action) => ActionRaised?.Invoke(this, action);
    }

    public event EventHandler<TrayAction>? ActionRaised;

    public event EventHandler? SystemResumed;

    internal TrayServiceLifecycleState LifecycleState
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        TrayRunState? restartAfter = null;
        Task? readiness = null;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);

            if (_currentRun is { } currentRun)
            {
                if (_state is TrayServiceLifecycleState.Stopping or TrayServiceLifecycleState.Faulted)
                {
                    restartAfter = currentRun;
                }
                else
                {
                    return WaitForReadiness(currentRun.Ready.Task, cancellationToken);
                }
            }
            else
            {
                var newRun = new TrayRunState(_timeProvider);
                newRun.Thread = new Thread(() => MessageLoop(newRun))
                {
                    IsBackground = true,
                    Name = "Batterio tray window"
                };
                newRun.ClassName = $"Batterio.Tray.{Environment.ProcessId}.{Guid.NewGuid():N}";
                _currentRun = newRun;
                _state = TrayServiceLifecycleState.Starting;

                try
                {
                    newRun.Thread.Start();
                }
                catch (Exception exception)
                {
                    _currentRun = null;
                    _state = TrayServiceLifecycleState.Idle;
                    newRun.Ready.TrySetException(exception);
                    newRun.Completion.TrySetResult(true);
                    newRun.Detached.TrySetResult(true);
                }

                readiness = newRun.Ready.Task;
            }
        }

        if (restartAfter is not null)
        {
            return StartAfterRunDetachesAsync(restartAfter, cancellationToken);
        }

        return WaitForReadiness(readiness!, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        TrayRunState? run;
        nint window;
        bool shouldPostClose;
        lock (_gate)
        {
            run = _currentRun;
            if (run is null)
            {
                return;
            }

            _state = TrayServiceLifecycleState.Stopping;

            run.RequestStop();
            window = run.Window;
            shouldPostClose = window != 0;
        }

        if (shouldPostClose)
        {
            PostCloseIfNeeded(run!, window);
        }

        await run!.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public TrayIconPlacement? GetIconPlacement()
    {
        nint window;
        lock (_gate)
        {
            var run = _currentRun;
            if (run is null || !run.IconAdded)
            {
                return null;
            }

            window = run.Window;
        }

        return window == 0 ? null : _platform.GetIconPlacement(window);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) == 0)
        {
            _ = DisposeCoreAsync();
        }

        return new ValueTask(_disposed.Task);
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
            await _clickArbiter.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Warning, "Tray service disposal encountered an error.", exception);
        }
        finally
        {
            lock (_gate)
            {
                if (_currentRun is null)
                {
                    _state = TrayServiceLifecycleState.Disposed;
                }
            }

            _disposed.TrySetResult(true);
        }
    }

    private Task StartAfterRunDetachesAsync(TrayRunState run, CancellationToken cancellationToken) =>
        StartAfterRunDetachesCoreAsync(run, cancellationToken);

    private async Task StartAfterRunDetachesCoreAsync(TrayRunState run, CancellationToken cancellationToken)
    {
        await run.Detached.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task WaitForReadiness(Task readiness, CancellationToken cancellationToken) =>
        cancellationToken.CanBeCanceled ? readiness.WaitAsync(cancellationToken) : readiness;

    private void MessageLoop(TrayRunState run)
    {
        try
        {
            run.Instance = _platform.GetModuleHandle();
            if (run.Instance == 0)
            {
                throw new InvalidOperationException("Windows could not resolve the Batterio module handle.");
            }

            run.Icon = _platform.LoadIcon(Path.Combine(AppContext.BaseDirectory, "batterio.ico"));
            if (run.Icon == 0)
            {
                throw new InvalidOperationException("Could not load the Batterio icon.");
            }

            run.TaskbarCreatedMessage = _platform.RegisterTaskbarCreatedMessage();
            if (run.TaskbarCreatedMessage == 0)
            {
                throw new InvalidOperationException("Windows could not register the TaskbarCreated message.");
            }

            run.WindowProcedure = (window, message) => HandleWindowMessage(run, window, message);
            run.ClassAtom = _platform.RegisterWindowClass(
                run.ClassName,
                run.Instance,
                run.Icon,
                run.TaskbarCreatedMessage,
                run.WindowProcedure);
            if (run.ClassAtom == 0)
            {
                throw new InvalidOperationException("Windows could not register the tray callback window class.");
            }

            var window = _platform.CreateCallbackWindow(run.ClassName, run.Instance);
            if (window == 0)
            {
                throw new InvalidOperationException("Windows could not create the tray callback window.");
            }
            lock (_gate)
            {
                run.Window = window;
            }

            if (run.StopRequested)
            {
                run.Ready.TrySetCanceled();
                PostCloseIfNeeded(run, window);
            }
            else if (TryAddIcon(run))
            {
                CompleteIconReadiness(run);
            }
            else
            {
                ScheduleIconRetry(run);
            }

            while (true)
            {
                var result = _platform.GetMessage(out var message);
                if (result == -1)
                {
                    throw new InvalidOperationException("The tray message loop could not retrieve a Windows message.");
                }

                if (result == 0)
                {
                    break;
                }

                _platform.TranslateMessage(message);
                _platform.DispatchMessage(message);
                if (run.FatalException is { } fatalException)
                {
                    throw fatalException;
                }
            }
        }
        catch (Exception exception)
        {
            run.Failure = run.FatalException ?? exception;
            _log.Log(BatterioLogLevel.Critical, "Tray service failed to initialize or run.", run.Failure);
            lock (_gate)
            {
                if (ReferenceEquals(_currentRun, run) && Volatile.Read(ref _disposeRequested) == 0)
                {
                    _state = TrayServiceLifecycleState.Faulted;
                }
            }
        }
        finally
        {
            CleanupNativeResources(run);

            if (!run.Ready.Task.IsCompleted)
            {
                if (run.Failure is not null)
                {
                    run.Ready.TrySetException(run.Failure);
                }
                else if (run.StopRequested)
                {
                    run.Ready.TrySetCanceled();
                }
                else
                {
                    run.Ready.TrySetException(
                        new InvalidOperationException("The tray service stopped before the icon became ready."));
                }
            }

            run.Completion.TrySetResult(true);

            lock (_gate)
            {
                if (ReferenceEquals(_currentRun, run))
                {
                    _currentRun = null;
                    _state = Volatile.Read(ref _disposeRequested) != 0
                        ? TrayServiceLifecycleState.Disposed
                        : TrayServiceLifecycleState.Idle;
                }
            }

            run.Detached.TrySetResult(true);
        }
    }

    private void CleanupNativeResources(TrayRunState run)
    {
        TryCleanup(() => CancelRetryTimer(run), "Could not cancel the tray icon retry timer during cleanup.");
        TryCleanup(() => RemoveIcon(run), "Could not remove the tray icon during cleanup.");

        var window = run.Window;
        if (window != 0)
        {
            TryCleanup(() => _platform.DestroyWindow(window), "Could not destroy the tray callback window.");
            lock (_gate)
            {
                run.Window = 0;
            }
        }

        if (run.ClassAtom != 0)
        {
            TryCleanup(() => _platform.UnregisterWindowClass(run.ClassName, run.Instance), "Could not unregister the tray callback window class.");
            run.ClassAtom = 0;
        }

        if (run.Icon != 0)
        {
            TryCleanup(() => _platform.DestroyIcon(run.Icon), "Could not destroy the loaded tray icon.");
            run.Icon = 0;
        }
    }

    private void TryCleanup(Func<bool> cleanup, string message)
    {
        try
        {
            if (!cleanup())
            {
                _log.Log(BatterioLogLevel.Warning, message);
            }
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Warning, message, exception);
        }
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

    private bool TryAddIcon(TrayRunState run)
    {
        if (run.IconAdded)
        {
            return true;
        }

        if (!_platform.TryAddIcon(run.Window, run.Icon, out var version4))
        {
            return false;
        }

        run.IconAdded = true;
        run.Version4 = version4;
        if (!version4)
        {
            _log.Log(BatterioLogLevel.Warning, "Tray icon was added but notification version 4 could not be selected.");
        }

        return true;
    }

    private void RetryIconRegistration(TrayRunState run)
    {
        CancelRetryTimer(run);
        if (run.IconAdded)
        {
            return;
        }

        if (TryAddIcon(run))
        {
            CompleteIconReadiness(run);
        }
        else
        {
            ScheduleIconRetry(run);
        }
    }

    private void RestoreIcon(TrayRunState run)
    {
        CancelRetryTimer(run);
        run.RetryController.Reset();
        run.RetryController.ResetFailureWarning();
        run.RetryAttempts = 0;
        run.RetryStartedAt = null;
        if (run.IconAdded)
        {
            RemoveIcon(run);
        }

        if (TryAddIcon(run))
        {
            CompleteIconReadiness(run);
        }
        else
        {
            ScheduleIconRetry(run);
        }
    }

    private void ScheduleIconRetry(TrayRunState run)
    {
        if (run.RetryAttempts == 0)
        {
            run.RetryStartedAt = _timeProvider.GetUtcNow();
        }

        var delay = run.RetryController.RecordFailure();
        var milliseconds = checked((uint)Math.Clamp(delay.TotalMilliseconds, 1, uint.MaxValue));
        if (_platform.StartRetryTimer(run.Window, RetryTimerId, milliseconds) == 0)
        {
            throw new InvalidOperationException("Windows could not create the tray icon retry timer.");
        }

        run.RetryController.MarkRetryScheduled();
        run.RetryTimerActive = true;
        run.RetryAttempts++;

        if (run.RetryController.ShouldLogFailure())
        {
            var level = run.RetryAttempts == 1 ? BatterioLogLevel.Error : BatterioLogLevel.Warning;
            _log.Log(level, $"Tray icon registration is unavailable; retry attempt {run.RetryAttempts} is scheduled.");
        }
    }

    private void CompleteIconReadiness(TrayRunState run)
    {
        if (run.StopRequested)
        {
            return;
        }

        if (run.RetryAttempts > 0)
        {
            var elapsed = run.RetryStartedAt.HasValue
                ? _timeProvider.GetUtcNow() - run.RetryStartedAt.Value
                : TimeSpan.Zero;
            _log.Log(BatterioLogLevel.Information,
                $"Tray icon registration recovered after {run.RetryAttempts} attempts in {elapsed.TotalSeconds:F1}s.");
        }

        run.RetryController.Reset();
        run.RetryController.ResetFailureWarning();
        run.RetryAttempts = 0;
        run.RetryStartedAt = null;
        if (!run.Ready.TrySetResult(true))
        {
            return;
        }

        lock (_gate)
        {
            if (ReferenceEquals(_currentRun, run) && _state == TrayServiceLifecycleState.Starting)
            {
                _state = TrayServiceLifecycleState.Ready;
            }
        }
    }

    private void CancelRetryTimer(TrayRunState run)
    {
        if (!run.RetryTimerActive)
        {
            return;
        }

        run.RetryTimerActive = false;
        run.RetryController.CancelRetry();
        if (run.Window != 0)
        {
            _ = _platform.CancelRetryTimer(run.Window, RetryTimerId);
        }
    }

    private void RemoveIcon(TrayRunState run)
    {
        if (!run.IconAdded || run.Window == 0)
        {
            return;
        }

        var removed = _platform.DeleteIcon(run.Window);
        run.IconAdded = false;
        run.Version4 = false;
        if (!removed)
        {
            _log.Log(BatterioLogLevel.Warning, "Windows did not confirm tray icon removal.");
        }
    }

    private nint HandleWindowMessage(TrayRunState run, nint window, TrayWindowEvent message)
    {
        try
        {
            switch (message.Kind)
            {
                case TrayWindowEventKind.TaskbarCreated:
                    RestoreIcon(run);
                    return 0;
                case TrayWindowEventKind.RetryTimer:
                    RetryIconRegistration(run);
                    return 0;
                case TrayWindowEventKind.Close:
                    run.RequestStop();
                    _platform.PostQuitMessage(0);
                    return 0;
                case TrayWindowEventKind.Destroy:
                    _platform.PostQuitMessage(0);
                    return 0;
                case TrayWindowEventKind.LeftButtonUp:
                case TrayWindowEventKind.LeftButtonDoubleClick:
                case TrayWindowEventKind.RightButtonUp:
                case TrayWindowEventKind.ContextMenu:
                case TrayWindowEventKind.KeyboardActivate:
                    HandleTrayCallback(run, message);
                    return 0;
                case TrayWindowEventKind.SystemResumed:
                    SystemResumed?.Invoke(this, EventArgs.Empty);
                    return 1;
                default:
                    return 0;
            }
        }
        catch (Exception exception)
        {
            run.FatalException ??= exception;
            try
            {
                _platform.PostQuitMessage(0);
            }
            catch
            {
                // The message loop will still observe a later native failure if quit posting failed.
            }

            return 0;
        }
    }

    private void HandleTrayCallback(TrayRunState run, TrayWindowEvent message)
    {
        switch (message.Kind)
        {
            case TrayWindowEventKind.LeftButtonUp:
                _ = _clickArbiter.HandleAsync(TrayNotification.LeftButtonUp);
                break;
            case TrayWindowEventKind.LeftButtonDoubleClick:
                _ = _clickArbiter.HandleAsync(TrayNotification.LeftButtonDoubleClick);
                break;
            case TrayWindowEventKind.RightButtonUp when !run.Version4:
                _ = _clickArbiter.HandleAsync(TrayNotification.RightButtonUp);
                break;
            case TrayWindowEventKind.ContextMenu:
                _ = _clickArbiter.HandleAsync(TrayNotification.RightButtonUp);
                break;
            case TrayWindowEventKind.KeyboardActivate:
                _ = _clickArbiter.HandleAsync(TrayNotification.KeyboardActivate);
                break;
        }
    }

    private void PostCloseIfNeeded(TrayRunState run, nint window)
    {
        if (!run.TryMarkClosePosted())
        {
            return;
        }

        try
        {
            if (!_platform.PostClose(window))
            {
                run.ResetClosePosted();
                _log.Log(BatterioLogLevel.Warning, "Windows did not accept the tray shutdown message.");
            }
        }
        catch (Exception exception)
        {
            run.ResetClosePosted();
            _log.Log(BatterioLogLevel.Warning, "Could not post the tray shutdown message.", exception);
        }
    }

    private sealed class TrayRunState(TimeProvider timeProvider)
    {
        private int _stopRequested;
        private int _closePosted;

        public readonly TaskCompletionSource<bool> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Detached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TrayIconRetryController RetryController = new(timeProvider);

        public Thread Thread = null!;
        public string ClassName = string.Empty;
        public TrayNativeWindowProcedure? WindowProcedure;
        public nint Instance;
        public nint Icon;
        public nint Window;
        public ushort ClassAtom;
        public uint TaskbarCreatedMessage;
        public volatile bool IconAdded;
        public bool Version4;
        public bool RetryTimerActive;
        public int RetryAttempts;
        public DateTimeOffset? RetryStartedAt;
        public Exception? Failure;
        public Exception? FatalException;

        public bool StopRequested => Volatile.Read(ref _stopRequested) != 0;

        public void RequestStop() => Interlocked.Exchange(ref _stopRequested, 1);

        public bool TryMarkClosePosted() => Interlocked.CompareExchange(ref _closePosted, 1, 0) == 0;

        public void ResetClosePosted() => Interlocked.Exchange(ref _closePosted, 0);
    }
}

namespace Batterio.App.Windows;

/// <summary>Runs the one-time runtime startup stages and shares their first result.</summary>
internal sealed class RuntimeStartupSequence
{
    private readonly object _gate = new();
    private readonly Action _hidePopup;
    private readonly Func<Task> _startTray;
    private readonly Func<Task> _initializeNotifications;
    private readonly Func<Task> _startSelectionAndPolling;
    private readonly Action<Exception>? _logFailure;
    private Task? _startupTask;

    public RuntimeStartupSequence(
        Action hidePopup,
        Func<Task> startTray,
        Func<Task> initializeNotifications,
        Func<Task> startSelectionAndPolling,
        Action<Exception>? logFailure = null)
    {
        _hidePopup = hidePopup ?? throw new ArgumentNullException(nameof(hidePopup));
        _startTray = startTray ?? throw new ArgumentNullException(nameof(startTray));
        _initializeNotifications = initializeNotifications ?? throw new ArgumentNullException(nameof(initializeNotifications));
        _startSelectionAndPolling = startSelectionAndPolling ?? throw new ArgumentNullException(nameof(startSelectionAndPolling));
        _logFailure = logFailure;
    }

    public Task StartAsync()
    {
        lock (_gate)
        {
            if (_startupTask is not null)
            {
                return _startupTask;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _startupTask = completion.Task;
            _ = RunAsync(completion);
            return _startupTask;
        }
    }

    private async Task RunAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            _hidePopup();
            await _startTray().ConfigureAwait(false);
            await _initializeNotifications().ConfigureAwait(false);
            await _startSelectionAndPolling().ConfigureAwait(false);
            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            try
            {
                _logFailure?.Invoke(exception);
            }
            catch
            {
                // A logging failure must not hide the startup failure from callers.
            }

            completion.TrySetException(exception);
        }
    }
}

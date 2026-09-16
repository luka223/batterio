namespace Batterio.App.Windows;

/// <summary>Applies the popup-created startup and launcher-readiness policy.</summary>
internal sealed class RuntimeStartupCompletionCoordinator
{
    private readonly object _gate = new();
    private readonly Func<Task> _startRuntime;
    private readonly Action _installActivationDispatcher;
    private readonly Func<bool> _signalReadiness;
    private readonly bool _supervised;
    private readonly Action<Exception> _showDeveloperError;
    private readonly Func<int, Task> _requestExit;
    private Task? _completionTask;

    public RuntimeStartupCompletionCoordinator(
        Func<Task> startRuntime,
        Action installActivationDispatcher,
        Func<bool> signalReadiness,
        bool supervised,
        Action<Exception> showDeveloperError,
        Func<int, Task> requestExit)
    {
        _startRuntime = startRuntime ?? throw new ArgumentNullException(nameof(startRuntime));
        _installActivationDispatcher = installActivationDispatcher ?? throw new ArgumentNullException(nameof(installActivationDispatcher));
        _signalReadiness = signalReadiness ?? throw new ArgumentNullException(nameof(signalReadiness));
        _supervised = supervised;
        _showDeveloperError = showDeveloperError ?? throw new ArgumentNullException(nameof(showDeveloperError));
        _requestExit = requestExit ?? throw new ArgumentNullException(nameof(requestExit));
    }

    public Task CompleteAsync()
    {
        lock (_gate)
        {
            if (_completionTask is not null)
            {
                return _completionTask;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _completionTask = completion.Task;
            _ = CompleteCoreAsync(completion);
            return _completionTask;
        }
    }

    private async Task CompleteCoreAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            await _startRuntime();
            _installActivationDispatcher();
            if (!_signalReadiness())
            {
                throw new InvalidOperationException("The launcher readiness handshake could not be signaled.");
            }

            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            if (_supervised)
            {
                await _requestExit(1);
            }
            else
            {
                _showDeveloperError(exception);
            }

            completion.TrySetResult(true);
        }
    }
}

internal interface IProcessTerminator
{
    void Exit(int exitCode);
}

internal sealed class EnvironmentProcessTerminator : IProcessTerminator
{
    public void Exit(int exitCode) => Environment.Exit(exitCode);
}

internal sealed class BoundedCleanupProcessExit
{
    private readonly IProcessTerminator _processTerminator;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public BoundedCleanupProcessExit(
        IProcessTerminator processTerminator,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _processTerminator = processTerminator ?? throw new ArgumentNullException(nameof(processTerminator));
        _delay = delay ?? ((timeout, cancellationToken) => Task.Delay(timeout, cancellationToken));
    }

    public async Task RunAsync(
        Func<Task> cleanup,
        TimeSpan timeout,
        int exitCode,
        Action<Exception> reportTimeout,
        Action<Exception> reportFailure)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        ArgumentNullException.ThrowIfNull(reportTimeout);
        ArgumentNullException.ThrowIfNull(reportFailure);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var timeoutCancellation = new CancellationTokenSource();
        try
        {
            var cleanupTask = cleanup();
            var timeoutTask = _delay(timeout, timeoutCancellation.Token);
            if (await Task.WhenAny(cleanupTask, timeoutTask).ConfigureAwait(false) == cleanupTask)
            {
                timeoutCancellation.Cancel();
                await cleanupTask.ConfigureAwait(false);
            }
            else
            {
                await timeoutTask.ConfigureAwait(false);
                ObserveLateCleanup(cleanupTask);
                reportTimeout(new TimeoutException("Application cleanup exceeded its bounded shutdown time."));
            }
        }
        catch (Exception exception)
        {
            reportFailure(exception);
        }
        finally
        {
            _processTerminator.Exit(exitCode);
        }
    }

    private static void ObserveLateCleanup(Task cleanupTask)
    {
        _ = cleanupTask.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

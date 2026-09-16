using System.Diagnostics;
using System.Runtime.InteropServices;
using Batterio.Core.Startup;

namespace Batterio.Startup;

public sealed record StartupProcessStartInfo(
    string ExecutablePath,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    string ReadyEventName);

public interface IStartupTaskbar
{
    bool IsAvailable();
}

public interface IStartupReadyEvent : IDisposable
{
    string Name { get; }

    bool IsSetNow();

    Task WaitAsync(CancellationToken cancellationToken = default);
}

public interface IStartupReadyEventFactory
{
    IStartupReadyEvent Create();
}

public interface IStartupChildProcess : IDisposable
{
    int ProcessId { get; }

    Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);

    void Terminate();
}

public interface IStartupProcessFactory
{
    IStartupChildProcess Start(StartupProcessStartInfo startInfo);
}

public interface IStartupDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

public interface IStartupLog
{
    void LauncherStarted(string version);

    void ShellUnavailableAfterTimeout();

    void AttemptCompleted(int attempt, int? processId, int? exitCode, bool readinessSignaled, string? failureType);

    void FinalFailure(int attempts);

    void RuntimeMissing();

    void UnexpectedFailure(string failureType);
}

/// <summary>
/// Supervises only the runtime's pre-readiness lifetime. Once readiness is signaled,
/// the launcher exits and never observes normal application shutdown.
/// </summary>
public sealed class StartupSupervisor
{
    public static IReadOnlyList<TimeSpan> RetryDelays { get; } =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    private static readonly TimeSpan ShellPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ShellWaitTimeout = TimeSpan.FromSeconds(60);
    public static TimeSpan StartupReadinessTimeout { get; } = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TerminationWaitTimeout = TimeSpan.FromSeconds(10);
    private readonly string _installationDirectory;
    private readonly string _runtimeExecutablePath;
    private readonly IStartupTaskbar _taskbar;
    private readonly IStartupReadyEventFactory _readyEvents;
    private readonly IStartupProcessFactory _processes;
    private readonly IStartupDelay _delay;
    private readonly IStartupLog _log;
    private readonly Func<string, bool> _fileExists;
    private readonly string _version;
    private readonly TimeSpan _startupReadinessTimeout;

    public StartupSupervisor(
        string installationDirectory,
        string runtimeExecutablePath,
        IStartupTaskbar taskbar,
        IStartupReadyEventFactory readyEvents,
        IStartupProcessFactory processes,
        IStartupDelay delay,
        IStartupLog log,
        Func<string, bool>? fileExists = null,
        string? version = null,
        TimeSpan? startupReadinessTimeout = null)
    {
        _installationDirectory = Path.GetFullPath(installationDirectory ?? throw new ArgumentNullException(nameof(installationDirectory)));
        _runtimeExecutablePath = Path.GetFullPath(runtimeExecutablePath ?? throw new ArgumentNullException(nameof(runtimeExecutablePath)));
        _taskbar = taskbar ?? throw new ArgumentNullException(nameof(taskbar));
        _readyEvents = readyEvents ?? throw new ArgumentNullException(nameof(readyEvents));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _fileExists = fileExists ?? File.Exists;
        _version = version ?? typeof(StartupSupervisor).Assembly.GetName().Version?.ToString() ?? "unknown";
        _startupReadinessTimeout = startupReadinessTimeout ?? StartupReadinessTimeout;
        if (_startupReadinessTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(startupReadinessTimeout));
        }
    }

    public async Task<int> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var forwardedArguments = arguments.ToArray();
        _log.LauncherStarted(_version);

        if (!_fileExists(_runtimeExecutablePath))
        {
            _log.RuntimeMissing();
            return 1;
        }

        if (!await WaitForShellAsync(cancellationToken).ConfigureAwait(false))
        {
            _log.ShellUnavailableAfterTimeout();
        }

        for (var index = 0; index < RetryDelays.Count; index++)
        {
            await _delay.DelayAsync(RetryDelays[index], cancellationToken).ConfigureAwait(false);
            var attempt = index + 1;
            var result = await RunAttemptAsync(attempt, forwardedArguments, cancellationToken).ConfigureAwait(false);
            if (result == StartupAttemptResult.Ready)
            {
                return 0;
            }

            if (result == StartupAttemptResult.Abort)
            {
                _log.FinalFailure(attempt);
                return 1;
            }
        }

        _log.FinalFailure(RetryDelays.Count);
        return 1;
    }

    private async Task<bool> WaitForShellAsync(CancellationToken cancellationToken)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < ShellWaitTimeout; elapsed += ShellPollInterval)
        {
            if (_taskbar.IsAvailable())
            {
                return true;
            }

            await _delay.DelayAsync(ShellPollInterval, cancellationToken).ConfigureAwait(false);
        }

        return _taskbar.IsAvailable();
    }

    private async Task<StartupAttemptResult> RunAttemptAsync(
        int attempt,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        IStartupReadyEvent? readyEvent = null;
        CancellationTokenSource? attemptWaitCancellation = null;
        IStartupChildProcess? child = null;
        Task<int>? processExit = null;
        Task? readiness = null;
        Task? readinessTimeout = null;
        try
        {
            readyEvent = _readyEvents.Create();
            attemptWaitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var startInfo = new StartupProcessStartInfo(
                _runtimeExecutablePath,
                _installationDirectory,
                arguments,
                readyEvent.Name);

            child = _processes.Start(startInfo);
            processExit = child.WaitForExitAsync(cancellationToken);
            readiness = readyEvent.WaitAsync(attemptWaitCancellation.Token);
            readinessTimeout = Task.Delay(_startupReadinessTimeout, attemptWaitCancellation.Token);

            var completed = await Task.WhenAny(processExit, readiness, readinessTimeout).ConfigureAwait(false);
            if (IsReady(readyEvent))
            {
                int? readyExitCode = null;
                if (processExit.IsCompleted)
                {
                    readyExitCode = await processExit.ConfigureAwait(false);
                }

                var readyFailure = readyExitCode is not null and not 0 ? "PostReadinessExit" : null;
                _log.AttemptCompleted(attempt, child.ProcessId, readyExitCode, readinessSignaled: true, readyFailure);
                return readyFailure is null ? StartupAttemptResult.Ready : StartupAttemptResult.Retry;
            }

            if (processExit.IsCompleted)
            {
                var exitCode = await processExit.ConfigureAwait(false);
                // The event and process can complete in the same scheduling turn. Check
                // the handle again after observing the exit before classifying the attempt.
                var signaledWithExit = IsReady(readyEvent);
                var failureType = signaledWithExit
                    ? exitCode == 0 ? null : "PostReadinessExit"
                    : "PreReadinessExit";
                _log.AttemptCompleted(attempt, child.ProcessId, exitCode, signaledWithExit, failureType);
                return failureType is null ? StartupAttemptResult.Ready : StartupAttemptResult.Retry;
            }

            if (ReferenceEquals(completed, readiness))
            {
                // Surface an event-wait failure instead of waiting forever for the child.
                await readiness.ConfigureAwait(false);
                throw new InvalidOperationException("The startup readiness wait completed without a signal.");
            }

            try
            {
                child.Terminate();
            }
            catch (Exception exception)
            {
                _log.AttemptCompleted(
                    attempt,
                    child.ProcessId,
                    null,
                    readinessSignaled: false,
                    $"ReadinessTimeoutTerminationFailure:{exception.GetType().Name}");
                return StartupAttemptResult.Abort;
            }

            var exitAfterTermination = await WaitForTerminationAsync(processExit, cancellationToken).ConfigureAwait(false);
            var timeoutFailure = exitAfterTermination is null
                ? "ReadinessTimeoutTerminationUnconfirmed"
                : "ReadinessTimeout";
            _log.AttemptCompleted(attempt, child.ProcessId, exitAfterTermination, readinessSignaled: false, timeoutFailure);
            return exitAfterTermination is null ? StartupAttemptResult.Abort : StartupAttemptResult.Retry;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failureType = exception.GetType().Name;
            int? exitCode = null;
            if (child is not null)
            {
                if (processExit?.IsCompletedSuccessfully == true)
                {
                    exitCode = await processExit.ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        child.Terminate();
                        var terminationExit = child.WaitForExitAsync(cancellationToken);
                        exitCode = await WaitForTerminationAsync(terminationExit, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception terminationException)
                    {
                        _log.AttemptCompleted(
                            attempt,
                            child.ProcessId,
                            null,
                            IsReady(readyEvent),
                            $"{failureType}:TerminationFailure:{terminationException.GetType().Name}");
                        return StartupAttemptResult.Abort;
                    }

                    if (exitCode is null)
                    {
                        failureType += ":TerminationUnconfirmed";
                    }
                }
            }

            _log.AttemptCompleted(attempt, child?.ProcessId, exitCode, IsReady(readyEvent), failureType);
            return child is not null && exitCode is null
                ? StartupAttemptResult.Abort
                : StartupAttemptResult.Retry;
        }
        finally
        {
            attemptWaitCancellation?.Cancel();
            if (readiness is not null)
            {
                try
                {
                    await readiness.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The child exited before readiness; stop polling the event before
                    // closing its handle.
                }
                catch
                {
                    // The attempt failure has already been recorded above.
                }
            }

            if (readinessTimeout is not null)
            {
                try
                {
                    await readinessTimeout.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // A readiness result or process exit ended the attempt first.
                }
            }

            child?.Dispose();
            attemptWaitCancellation?.Dispose();
            readyEvent?.Dispose();
        }

    }

    private static bool IsReady(IStartupReadyEvent? readyEvent)
    {
        try
        {
            return readyEvent?.IsSetNow() == true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int?> WaitForTerminationAsync(Task<int> processExit, CancellationToken cancellationToken)
    {
        var terminationTimeout = Task.Delay(TerminationWaitTimeout, cancellationToken);
        var completed = await Task.WhenAny(processExit, terminationTimeout).ConfigureAwait(false);
        if (ReferenceEquals(completed, processExit))
        {
            return await processExit.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    private enum StartupAttemptResult
    {
        Ready,
        Retry,
        Abort
    }
}

public sealed class StartupDelay : IStartupDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);
}

public sealed class NativeStartupTaskbar : IStartupTaskbar
{
    public bool IsAvailable() => FindWindow(StartupConstants.TrayWindowClass, null) != 0;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? className, string? windowName);
}

public sealed class NativeStartupProcessFactory : IStartupProcessFactory
{
    public IStartupChildProcess Start(StartupProcessStartInfo startInfo)
    {
        var processInfo = new ProcessStartInfo
        {
            FileName = startInfo.ExecutablePath,
            WorkingDirectory = startInfo.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in startInfo.Arguments)
        {
            processInfo.ArgumentList.Add(argument);
        }

        processInfo.Environment[StartupConstants.StartupReadyEventEnvironmentVariable] = startInfo.ReadyEventName;
        var process = Process.Start(processInfo)
            ?? throw new InvalidOperationException("The Batterio runtime process could not be started.");
        return new NativeStartupChildProcess(process);
    }

    private sealed class NativeStartupChildProcess(Process process) : IStartupChildProcess
    {
        private readonly Process _process = process;

        public int ProcessId => _process.Id;

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return _process.ExitCode;
        }

        public void Terminate()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }

        public void Dispose() => _process.Dispose();
    }
}

public sealed class NamedStartupReadyEventFactory : IStartupReadyEventFactory
{
    public IStartupReadyEvent Create() => new NamedStartupReadyEvent();

    private sealed class NamedStartupReadyEvent : IStartupReadyEvent
    {
        private readonly EventWaitHandle _handle;

        public NamedStartupReadyEvent()
        {
            Name = $"Batterio.StartupReady.{Guid.NewGuid():N}";
            _handle = new EventWaitHandle(false, EventResetMode.ManualReset, Name, out _);
        }

        public string Name { get; }

        public bool IsSetNow() => _handle.WaitOne(0);

        public async Task WaitAsync(CancellationToken cancellationToken = default)
        {
            while (!IsSetNow())
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }

        public void Dispose() => _handle.Dispose();
    }
}

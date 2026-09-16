using Batterio.Core.Startup;
using Batterio.Startup;

namespace Batterio.Startup.Tests;

public sealed class StartupSupervisorTests
{
    [Fact]
    public async Task ImmediateReadinessReturnsAndDoesNotObserveLaterExit()
    {
        var harness = new Harness([ProcessOutcomeSpec.Ready]);

        var result = await harness.RunAsync("one", StartupConstants.ToastActivationArgument);

        Assert.Equal(0, result);
        Assert.Single(harness.Processes.Starts);
        Assert.Equal(1, harness.Processes.DisposedCount);
        var child = Assert.Single(harness.Processes.Children);
        Assert.True(child.IsAlive);
        child.ExitNormally();
        await Task.Yield();
        Assert.Single(harness.Processes.Starts);
        Assert.Equal([TimeSpan.Zero], harness.Delays.Delays.Take(1));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task ReadinessOnAnyLaterAttemptStopsAtThatAttempt(int successfulAttempt)
    {
        var outcomes = Enumerable.Repeat(ProcessOutcomeSpec.Exit(17), successfulAttempt - 1)
            .Append(ProcessOutcomeSpec.Ready)
            .ToArray();
        var harness = new Harness(outcomes);

        var result = await harness.RunAsync();

        Assert.Equal(0, result);
        Assert.Equal(successfulAttempt, harness.Processes.Starts.Count);
        Assert.Equal(successfulAttempt, harness.Log.Attempts.Count);
        Assert.True(harness.Log.Attempts[^1].ReadinessSignaled);
    }

    [Fact]
    public async Task FivePreReadinessFailuresProduceNoSixthLaunch()
    {
        var harness = new Harness(Enumerable.Repeat(ProcessOutcomeSpec.Exit(23), 5).ToArray());

        var result = await harness.RunAsync();

        Assert.Equal(1, result);
        Assert.Equal(5, harness.Processes.Starts.Count);
        Assert.Equal(5, harness.Log.Attempts.Count);
        Assert.Equal(1, harness.Log.FinalFailureCount);
        Assert.Equal(StartupSupervisor.RetryDelays, harness.Delays.Delays);
    }

    [Fact]
    public async Task ReadinessSetWithSuccessfulProcessExitIsClassifiedAsSuccess()
    {
        var harness = new Harness([ProcessOutcomeSpec.ExitWithReadiness(0)]);

        var result = await harness.RunAsync();

        Assert.Equal(0, result);
        var attempt = Assert.Single(harness.Log.Attempts);
        Assert.True(attempt.ReadinessSignaled);
        Assert.Equal(0, attempt.ExitCode);
        Assert.Null(attempt.FailureType);
    }

    [Fact]
    public async Task ReadinessSetWithFailedProcessExitRetries()
    {
        var harness = new Harness([
            ProcessOutcomeSpec.ExitWithReadiness(9),
            ProcessOutcomeSpec.Ready
        ]);

        var result = await harness.RunAsync();

        Assert.Equal(0, result);
        Assert.Equal(2, harness.Processes.Starts.Count);
        Assert.Equal("PostReadinessExit", harness.Log.Attempts[0].FailureType);
        Assert.Equal(9, harness.Log.Attempts[0].ExitCode);
        Assert.True(harness.Log.Attempts[0].ReadinessSignaled);
        Assert.Null(harness.Log.Attempts[1].FailureType);
    }

    [Fact]
    public async Task ReadinessTimeoutTerminatesEachChildAndStopsAfterFiveAttempts()
    {
        var harness = new Harness(
            [ProcessOutcomeSpec.Hang],
            startupReadinessTimeout: TimeSpan.Zero);

        var result = await harness.RunAsync();

        Assert.Equal(1, result);
        Assert.Equal(5, harness.Processes.Starts.Count);
        Assert.All(harness.Processes.Children, child => Assert.Equal(1, child.TerminateCount));
        Assert.All(harness.Log.Attempts, attempt => Assert.Equal("ReadinessTimeout", attempt.FailureType));
        Assert.Equal(1, harness.Log.FinalFailureCount);
    }

    [Fact]
    public async Task FailedTimeoutTerminationDoesNotStartADuplicateChild()
    {
        var harness = new Harness(
            [ProcessOutcomeSpec.Hang],
            startupReadinessTimeout: TimeSpan.Zero,
            terminationThrows: true);

        var result = await harness.RunAsync();

        Assert.Equal(1, result);
        Assert.Single(harness.Processes.Starts);
        var attempt = Assert.Single(harness.Log.Attempts);
        Assert.Equal("ReadinessTimeoutTerminationFailure:InvalidOperationException", attempt.FailureType);
        Assert.Equal(1, harness.Log.FinalFailureCount);
    }

    [Fact]
    public async Task ReadyEventSetupFailureIsLoggedAndRetried()
    {
        var harness = new Harness([ProcessOutcomeSpec.Ready], readyEventFailures: 1);

        var result = await harness.RunAsync();

        Assert.Equal(0, result);
        Assert.Single(harness.Processes.Starts);
        Assert.Equal(2, harness.Log.Attempts.Count);
        Assert.Equal("IOException", harness.Log.Attempts[0].FailureType);
        Assert.Null(harness.Log.Attempts[0].ProcessId);
        Assert.Equal([TimeSpan.Zero, TimeSpan.FromSeconds(1)], harness.Delays.Delays);
    }

    [Fact]
    public async Task MissingRuntimeDoesNotStartAChild()
    {
        var harness = new Harness([ProcessOutcomeSpec.Ready], runtimeExists: false);

        var result = await harness.RunAsync();

        Assert.Equal(1, result);
        Assert.Empty(harness.Processes.Starts);
        Assert.Equal(1, harness.Log.RuntimeMissingCount);
    }

    [Fact]
    public async Task MissingTaskbarWaitsSixtySecondsThenStillLaunches()
    {
        var harness = new Harness([ProcessOutcomeSpec.Ready], taskbarAvailable: false);

        var result = await harness.RunAsync();

        Assert.Equal(0, result);
        Assert.Equal(1, harness.Log.ShellTimeoutCount);
        Assert.Equal(TimeSpan.FromSeconds(60), harness.Delays.Delays.Take(60).Aggregate(TimeSpan.Zero, (sum, delay) => sum + delay));
        Assert.Single(harness.Processes.Starts);
    }

    [Fact]
    public async Task ArgumentsWorkingDirectoryAndReadyEnvironmentAreForwarded()
    {
        var harness = new Harness([ProcessOutcomeSpec.Ready]);

        var result = await harness.RunAsync("first argument", StartupConstants.ToastActivationArgument);

        Assert.Equal(0, result);
        var start = Assert.Single(harness.Processes.Starts);
        Assert.Equal(Path.Combine(harness.InstallationDirectory, StartupConstants.RuntimeExecutableName), start.ExecutablePath);
        Assert.Equal(harness.InstallationDirectory, start.WorkingDirectory);
        Assert.Equal(["first argument", StartupConstants.ToastActivationArgument], start.Arguments);
        Assert.StartsWith("Batterio.StartupReady.", start.ReadyEventName, StringComparison.Ordinal);
    }

    private enum ProcessBehavior
    {
        Ready,
        Hang,
        ExitCode,
        ExitWithReadinessCode
    }

    private readonly record struct ProcessOutcomeSpec(ProcessBehavior Kind, int ExitCode)
    {
        public static ProcessOutcomeSpec Ready => new(ProcessBehavior.Ready, 0);
        public static ProcessOutcomeSpec Hang => new(ProcessBehavior.Hang, 0);
        public static ProcessOutcomeSpec Exit(int code) => new(ProcessBehavior.ExitCode, code);
        public static ProcessOutcomeSpec ExitWithReadiness(int code) => new(ProcessBehavior.ExitWithReadinessCode, code);
    }

    private sealed class Harness
    {
        public Harness(
            IReadOnlyList<ProcessOutcomeSpec> outcomes,
            bool runtimeExists = true,
            bool taskbarAvailable = true,
            int readyEventFailures = 0,
            TimeSpan? startupReadinessTimeout = null,
            bool terminationThrows = false)
        {
            InstallationDirectory = Path.Combine(Path.GetTempPath(), "Batterio.Startup.Tests", Guid.NewGuid().ToString("N"));
            Events = new FakeReadyEventFactory(readyEventFailures);
            Processes = new FakeProcessFactory(Events, outcomes, terminationThrows);
            Log = new RecordingLog();
            Delays = new RecordingDelay();
            Supervisor = new StartupSupervisor(
                InstallationDirectory,
                Path.Combine(InstallationDirectory, StartupConstants.RuntimeExecutableName),
                new FakeTaskbar(taskbarAvailable),
                Events,
                Processes,
                Delays,
                Log,
                _ => runtimeExists,
                version: "test",
                startupReadinessTimeout: startupReadinessTimeout);
        }

        public string InstallationDirectory { get; }
        public FakeReadyEventFactory Events { get; }
        public FakeProcessFactory Processes { get; }
        public RecordingLog Log { get; }
        public RecordingDelay Delays { get; }
        public StartupSupervisor Supervisor { get; }

        public Task<int> RunAsync(params string[] arguments) => Supervisor.RunAsync(arguments);
    }

    private sealed class FakeTaskbar(bool available) : IStartupTaskbar
    {
        public bool IsAvailable() => available;
    }

    private sealed class RecordingDelay : IStartupDelay
    {
        public List<TimeSpan> Delays { get; } = [];
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReadyEventFactory(int failuresRemaining) : IStartupReadyEventFactory
    {
        private int _failuresRemaining = failuresRemaining;
        public List<FakeReadyEvent> Created { get; } = [];

        public IStartupReadyEvent Create()
        {
            if (_failuresRemaining-- > 0)
            {
                throw new IOException("Simulated ready-event setup failure.");
            }

            var ready = new FakeReadyEvent($"Batterio.StartupReady.{Guid.NewGuid():N}");
            Created.Add(ready);
            return ready;
        }
    }

    private sealed class FakeReadyEvent(string name) : IStartupReadyEvent
    {
        private readonly TaskCompletionSource _signaled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name { get; } = name;
        public bool IsSetNow => _signaled.Task.IsCompleted;

        bool IStartupReadyEvent.IsSetNow() => IsSetNow;

        public Task WaitAsync(CancellationToken cancellationToken = default) =>
            _signaled.Task.WaitAsync(cancellationToken);

        public void Signal() => _signaled.TrySetResult();

        public void Dispose() { }
    }

    private sealed class FakeProcessFactory(
        FakeReadyEventFactory events,
        IReadOnlyList<ProcessOutcomeSpec> outcomes,
        bool terminationThrows) : IStartupProcessFactory
    {
        private int _nextOutcome;
        public List<StartupProcessStartInfo> Starts { get; } = [];
        public int DisposedCount { get; private set; }
        public List<FakeChildProcess> Children { get; } = [];

        public IStartupChildProcess Start(StartupProcessStartInfo startInfo)
        {
            Starts.Add(startInfo);
            var outcome = outcomes[Math.Min(_nextOutcome++, outcomes.Count - 1)];
            var ready = events.Created[^1];
            if (outcome.Kind is ProcessBehavior.Ready or ProcessBehavior.ExitWithReadinessCode)
            {
                ready.Signal();
            }

            var child = new FakeChildProcess(
                outcome.ExitCode,
                outcome.Kind is ProcessBehavior.ExitCode or ProcessBehavior.ExitWithReadinessCode,
                terminationThrows,
                () => DisposedCount++);
            Children.Add(child);
            return child;
        }
    }

    private sealed class FakeChildProcess(int exitCode, bool exits, bool terminationThrows, Action disposed) : IStartupChildProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsAlive { get; private set; } = !exits;
        public int TerminateCount { get; private set; }

        public int ProcessId { get; } = Random.Shared.Next(1000, 9999);

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            if (exits)
            {
                IsAlive = false;
                _exit.TrySetResult(exitCode);
            }

            return _exit.Task;
        }

        public void Terminate()
        {
            TerminateCount++;
            if (terminationThrows)
            {
                throw new InvalidOperationException("Simulated termination failure.");
            }

            IsAlive = false;
            _exit.TrySetResult(-1);
        }

        public void Dispose() => disposed();

        public void ExitNormally()
        {
            IsAlive = false;
            _exit.TrySetResult(0);
        }
    }

    private sealed class RecordingLog : IStartupLog
    {
        public List<Attempt> Attempts { get; } = [];
        public int FinalFailureCount { get; private set; }
        public int RuntimeMissingCount { get; private set; }
        public int ShellTimeoutCount { get; private set; }
        public int UnexpectedFailureCount { get; private set; }

        public void LauncherStarted(string version) { }
        public void ShellUnavailableAfterTimeout() => ShellTimeoutCount++;
        public void AttemptCompleted(int attempt, int? processId, int? exitCode, bool readinessSignaled, string? failureType) =>
            Attempts.Add(new(attempt, processId, exitCode, readinessSignaled, failureType));
        public void FinalFailure(int attempts) => FinalFailureCount++;
        public void RuntimeMissing() => RuntimeMissingCount++;
        public void UnexpectedFailure(string failureType) => UnexpectedFailureCount++;

        public readonly record struct Attempt(int Number, int? ProcessId, int? ExitCode, bool ReadinessSignaled, string? FailureType);
    }
}

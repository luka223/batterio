using Batterio.App.Windows;
using System.Collections.Concurrent;

namespace Batterio.Windows.Tests;

public sealed class RuntimeStartupTests
{
    [Fact]
    public async Task ConcurrentStartupCallsShareOneOrderedSequenceAndWaitForEveryStage()
    {
        var stages = new ConcurrentQueue<string>();
        var trayStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var trayReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationsStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationsReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var selectionStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var selectionReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sequence = new RuntimeStartupSequence(
            () => stages.Enqueue("hide-popup"),
            () =>
            {
                stages.Enqueue("tray");
                trayStarted.TrySetResult(true);
                return trayReady.Task;
            },
            () =>
            {
                stages.Enqueue("notifications");
                notificationsStarted.TrySetResult(true);
                return notificationsReady.Task;
            },
            () =>
            {
                stages.Enqueue("selection-polling");
                selectionStarted.TrySetResult(true);
                return selectionReady.Task;
            });

        var first = sequence.StartAsync();
        var second = sequence.StartAsync();
        Assert.Same(first, second);
        await trayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(["hide-popup", "tray"], stages.ToArray());

        trayReady.SetResult(true);
        await notificationsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(first.IsCompleted);
        notificationsReady.SetResult(true);
        await selectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(first.IsCompleted);
        selectionReady.SetResult(true);
        await Task.WhenAll(first, second);

        Assert.Equal(["hide-popup", "tray", "notifications", "selection-polling"], stages.ToArray());
    }

    [Fact]
    public async Task StartupFailureIsSharedAndStopsLaterStages()
    {
        var failure = new InvalidOperationException("tray startup failed");
        var calls = new List<string>();
        var logged = new List<Exception>();
        var sequence = new RuntimeStartupSequence(
            () => calls.Add("hide"),
            () =>
            {
                calls.Add("tray");
                return Task.FromException(failure);
            },
            () =>
            {
                calls.Add("notifications");
                return Task.CompletedTask;
            },
            () =>
            {
                calls.Add("selection");
                return Task.CompletedTask;
            },
            logged.Add);

        var first = sequence.StartAsync();
        var second = sequence.StartAsync();
        Assert.Same(first, second);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => first));

        Assert.Equal(["hide", "tray"], calls);
        Assert.Equal([failure], logged);
    }

    [Fact]
    public async Task LoggingFailureDoesNotHideStartupFailureOrLeaveSharedTaskPending()
    {
        var startupFailure = new InvalidOperationException("tray startup failed");
        var loggingFailure = new IOException("startup log is unavailable");
        var sequence = new RuntimeStartupSequence(
            () => { },
            () => Task.FromException(startupFailure),
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            _ => throw loggingFailure);

        var startupTask = sequence.StartAsync();
        var actualFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => startupTask.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Same(startupFailure, actualFailure);
        Assert.Same(startupTask, sequence.StartAsync());
    }

    [Fact]
    public async Task ReadinessFollowsSuccessfulStartupAndDispatcherInstallationExactlyOnce()
    {
        var calls = new List<string>();
        var startup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new RuntimeStartupCompletionCoordinator(
            () =>
            {
                calls.Add("startup");
                return startup.Task;
            },
            () => calls.Add("dispatcher"),
            () =>
            {
                calls.Add("readiness");
                return true;
            },
            supervised: true,
            _ => calls.Add("developer-error"),
            code =>
            {
                calls.Add($"exit-{code}");
                return Task.CompletedTask;
            });

        var first = coordinator.CompleteAsync();
        var second = coordinator.CompleteAsync();
        Assert.Same(first, second);
        Assert.Equal(["startup"], calls);
        startup.SetResult(true);
        await Task.WhenAll(first, second);

        Assert.Equal(["startup", "dispatcher", "readiness"], calls);
    }

    [Fact]
    public async Task SupervisedStartupFailureRequestsExitOneWithoutInstallingDispatcher()
    {
        var failure = new IOException("startup failed");
        var calls = new List<string>();
        var processExit = new RecordingProcessTerminator(code => calls.Add($"exit-{code}"));
        var boundedExit = new BoundedCleanupProcessExit(
            processExit,
            (_, _) => Task.CompletedTask);
        var coordinator = new RuntimeStartupCompletionCoordinator(
            () => Task.FromException(failure),
            () => calls.Add("dispatcher"),
            () =>
            {
                calls.Add("readiness");
                return true;
            },
            supervised: true,
            _ => calls.Add("developer-error"),
            code => boundedExit.RunAsync(
                () =>
                {
                    calls.Add("cleanup");
                    return Task.CompletedTask;
                },
                TimeSpan.FromSeconds(3),
                code,
                exception => calls.Add("cleanup-timeout"),
                exception => calls.Add("cleanup-failure")));

        await coordinator.CompleteAsync();

        Assert.Equal(["cleanup", "exit-1"], calls);
        Assert.Equal([1], processExit.ExitCodes);
    }

    [Fact]
    public async Task BoundedCleanupTimeoutStillRequestsExitWithoutWaitingForCleanup()
    {
        var cleanup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutReports = new List<Exception>();
        var cleanupFailures = new List<Exception>();
        var processExit = new RecordingProcessTerminator();
        var boundedExit = new BoundedCleanupProcessExit(processExit, (_, _) => Task.CompletedTask);

        await boundedExit.RunAsync(
            () => cleanup.Task,
            TimeSpan.FromSeconds(3),
            1,
            timeoutReports.Add,
            cleanupFailures.Add);

        Assert.False(cleanup.Task.IsCompleted);
        Assert.IsType<TimeoutException>(Assert.Single(timeoutReports));
        Assert.Empty(cleanupFailures);
        Assert.Equal([1], processExit.ExitCodes);

        cleanup.SetResult(true);
    }

    [Fact]
    public async Task DeveloperStartupFailureKeepsPopupErrorWithoutForcedExit()
    {
        var failure = new InvalidOperationException("developer startup failed");
        Exception? shown = null;
        var exitRequested = false;
        var dispatcherInstalled = false;
        var coordinator = new RuntimeStartupCompletionCoordinator(
            () => Task.FromException(failure),
            () => dispatcherInstalled = true,
            () => true,
            supervised: false,
            exception => shown = exception,
            _ =>
            {
                exitRequested = true;
                return Task.CompletedTask;
            });

        await coordinator.CompleteAsync();

        Assert.Same(failure, shown);
        Assert.False(dispatcherInstalled);
        Assert.False(exitRequested);
    }

    [Fact]
    public async Task ReadinessFailureUsesTheSameSupervisedFailurePath()
    {
        var exitCode = 0;
        var installed = false;
        var coordinator = new RuntimeStartupCompletionCoordinator(
            () => Task.CompletedTask,
            () => installed = true,
            () => false,
            supervised: true,
            _ => { },
            code =>
            {
                exitCode = code;
                return Task.CompletedTask;
            });

        await coordinator.CompleteAsync();

        Assert.True(installed);
        Assert.Equal(1, exitCode);
    }

    private sealed class RecordingProcessTerminator(Action<int>? onExit = null) : IProcessTerminator
    {
        private readonly List<int> _exitCodes = [];

        public IReadOnlyList<int> ExitCodes => _exitCodes;

        public void Exit(int exitCode)
        {
            _exitCodes.Add(exitCode);
            onExit?.Invoke(exitCode);
        }
    }

}

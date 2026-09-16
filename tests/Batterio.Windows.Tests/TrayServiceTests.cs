using System.Collections.Concurrent;
using Batterio.App.Windows;
using Batterio.Core;
using Batterio.Core.Abstractions;

namespace Batterio.Windows.Tests;

public sealed class TrayServiceTests
{
    [Fact]
    public async Task FailedAddAttemptsUseOneTrayThreadAndRecoverAfterOneTwoAndFiveSeconds()
    {
        var platform = new ControlledTrayNativePlatform();
        platform.EnqueueAddResults(false, false, false, true);
        var service = CreateService(platform);
        try
        {
            var ready = service.StartAsync();
            await platform.WaitForCallAsync("SetTimer:1000");
            await platform.SendWindowEventAsync(TrayWindowEventKind.RetryTimer);
            await platform.WaitForCallAsync("SetTimer:2000");
            await platform.SendWindowEventAsync(TrayWindowEventKind.RetryTimer);
            await platform.WaitForCallAsync("SetTimer:5000");
            await platform.SendWindowEventAsync(TrayWindowEventKind.RetryTimer);
            await ready.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal([1000, 2000, 5000], platform.Calls
                .Where(call => call.StartsWith("SetTimer:", StringComparison.Ordinal))
                .Select(call => int.Parse(call["SetTimer:".Length..])));
            Assert.Equal(4, platform.CountCalls("NIM_ADD"));
            Assert.Equal(1, platform.RunCount);
            Assert.Equal(TrayServiceLifecycleState.Ready, service.LifecycleState);
        }
        finally
        {
            await service.StopAsync();
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task BackoffReachesAndRemainsCappedAtThirtySeconds()
    {
        var platform = new ControlledTrayNativePlatform();
        platform.EnqueueAddResults(Enumerable.Repeat(false, 7).ToArray());
        var service = CreateService(platform);
        var ready = service.StartAsync();
        try
        {
            await platform.WaitForCallAsync("SetTimer:1000");
            for (var attempt = 1; attempt < 7; attempt++)
            {
                await platform.SendWindowEventAsync(TrayWindowEventKind.RetryTimer);
                var nextDelay = attempt switch
                {
                    1 => 2000,
                    2 => 5000,
                    3 => 10000,
                    _ => 30000
                };
                var delayOccurrence = nextDelay == 30000 ? attempt - 3 : 1;
                await platform.WaitForCallAsync($"SetTimer:{nextDelay}", delayOccurrence);
            }

            Assert.Equal([1000, 2000, 5000, 10000, 30000, 30000, 30000], platform.Calls
                .Where(call => call.StartsWith("SetTimer:", StringComparison.Ordinal))
                .Select(call => int.Parse(call["SetTimer:".Length..])));
        }
        finally
        {
            await service.StopAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ready);
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task TaskbarCreatedCancelsPendingTimerResetsCountersAndRetriesImmediately()
    {
        var platform = new ControlledTrayNativePlatform();
        platform.EnqueueAddResults(false, false, true);
        var service = CreateService(platform);
        try
        {
            var ready = service.StartAsync();
            await platform.WaitForCallAsync("SetTimer:1000");
            await platform.SendWindowEventAsync(TrayWindowEventKind.TaskbarCreated);
            await platform.WaitForCallAsync("SetTimer:1000", 2);
            await platform.SendWindowEventAsync(TrayWindowEventKind.RetryTimer);
            await ready.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(3, platform.CountCalls("NIM_ADD"));
            Assert.Equal(2, platform.CountCalls("SetTimer:1000"));
            Assert.Equal(2, platform.CountCalls("KillTimer"));
            Assert.Equal(1, platform.RunCount);
        }
        finally
        {
            await service.StopAsync();
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExplorerRestartDeletesStaleIconAndRestoresItWithoutAnotherThread()
    {
        var platform = new ControlledTrayNativePlatform();
        var service = CreateService(platform);
        try
        {
            await service.StartAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await platform.SendWindowEventAsync(TrayWindowEventKind.TaskbarCreated);

            Assert.Equal(2, platform.CountCalls("NIM_ADD"));
            Assert.Equal(1, platform.CountCalls("NIM_DELETE"));
            Assert.Equal(1, platform.RunCount);
            Assert.Equal(TrayServiceLifecycleState.Ready, service.LifecycleState);
        }
        finally
        {
            await service.StopAsync();
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task VersionSelectionFailureWarnsButStillCompletesReadiness()
    {
        var platform = new ControlledTrayNativePlatform { Version4SelectionSucceeds = false };
        var log = new RecordingLog();
        var service = CreateService(platform, log: log);
        try
        {
            await service.StartAsync().WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Contains(log.Entries, entry => entry.Level == BatterioLogLevel.Warning &&
                entry.Message.Contains("version 4", StringComparison.Ordinal));
            Assert.Equal(TrayServiceLifecycleState.Ready, service.LifecycleState);
        }
        finally
        {
            await service.StopAsync();
            await service.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("icon")]
    [InlineData("class")]
    [InlineData("window")]
    [InlineData("timer")]
    [InlineData("message")]
    public async Task FatalNativeFailuresCompleteTheRunAndCleanUpResources(string failurePoint)
    {
        var platform = new ControlledTrayNativePlatform();
        switch (failurePoint)
        {
            case "icon":
                platform.FailNextIconLoad = true;
                break;
            case "class":
                platform.FailNextClassRegistration = true;
                break;
            case "window":
                platform.FailNextWindowCreation = true;
                break;
            case "timer":
                platform.EnqueueAddResults(false);
                platform.FailNextTimerCreation = true;
                break;
            case "message":
                platform.EnqueueAddResults(false);
                platform.FailNextMessageRetrieval = true;
                break;
        }

        var service = CreateService(platform);
        try
        {
            var start = service.StartAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => start.WaitAsync(TimeSpan.FromSeconds(3)));

            if (failurePoint == "class")
            {
                Assert.Equal(0, platform.CountCalls("UnregisterClass"));
            }
            else if (failurePoint != "icon")
            {
                await platform.WaitForCallAsync("UnregisterClass");
            }

            if (failurePoint != "icon")
            {
                await platform.WaitForCallAsync("DestroyIcon");
            }

            Assert.True(service.LifecycleState is TrayServiceLifecycleState.Idle or TrayServiceLifecycleState.Faulted);
        }
        finally
        {
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task RegistrationWarningsAreLimitedToOncePerSimulatedMinute()
    {
        var platform = new ControlledTrayNativePlatform();
        platform.EnqueueAddResults(false, false, false);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var log = new RecordingLog();
        var service = CreateService(platform, clock, log);
        var ready = service.StartAsync();
        try
        {
            await platform.WaitForCallAsync("SetTimer:1000");
            clock.Advance(TimeSpan.FromSeconds(59));
            await platform.SendWindowEventAsync(TrayWindowEventKind.RetryTimer);
            await platform.WaitForCallAsync("SetTimer:2000");
            Assert.Equal(1, log.Entries.Count(entry => entry.Message.Contains("registration is unavailable", StringComparison.Ordinal)));

            clock.Advance(TimeSpan.FromSeconds(1));
            await platform.SendWindowEventAsync(TrayWindowEventKind.RetryTimer);
            await platform.WaitForCallAsync("SetTimer:5000");
            Assert.Equal(2, log.Entries.Count(entry => entry.Message.Contains("registration is unavailable", StringComparison.Ordinal)));
        }
        finally
        {
            await service.StopAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ready);
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task StopBeforeWindowCreationCancelsReadinessAndCleansUpInOrder()
    {
        var platform = new ControlledTrayNativePlatform { BlockIconLoad = true };
        var service = CreateService(platform);
        try
        {
            var ready = service.StartAsync();
            await platform.IconLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var stopped = service.StopAsync();
            Assert.False(stopped.IsCompleted);
            platform.ContinueIconLoad.Set();
            await stopped.WaitAsync(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ready);

            AssertOrdered(platform.Calls, "PostClose", "PostQuit", "DestroyWindow", "UnregisterClass", "DestroyIcon");
            Assert.Equal(0, platform.CountCalls("NIM_ADD"));
            Assert.Equal(TrayServiceLifecycleState.Idle, service.LifecycleState);
        }
        finally
        {
            platform.ContinueIconLoad.Set();
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task StopDuringPendingRetryCancelsTimerAndPerformsOrderedCleanup()
    {
        var platform = new ControlledTrayNativePlatform();
        platform.EnqueueAddResults(false);
        var service = CreateService(platform);
        try
        {
            var ready = service.StartAsync();
            await platform.WaitForCallAsync("SetTimer:1000");
            await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ready);

            AssertOrdered(platform.Calls, "KillTimer", "DestroyWindow", "UnregisterClass", "DestroyIcon");
            Assert.Equal(0, platform.CountCalls("NIM_DELETE"));
            Assert.Equal(TrayServiceLifecycleState.Idle, service.LifecycleState);
        }
        finally
        {
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task SameServiceStartsWithFreshRunsAfterFatalFailureAndNormalStop()
    {
        var platform = new ControlledTrayNativePlatform { FailNextIconLoad = true };
        var service = CreateService(platform);
        try
        {
            var failedRun = service.StartAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => failedRun.WaitAsync(TimeSpan.FromSeconds(3)));

            var afterFault = service.StartAsync();
            Assert.NotSame(failedRun, afterFault);
            await afterFault.WaitAsync(TimeSpan.FromSeconds(3));
            await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));

            var afterStop = service.StartAsync();
            Assert.NotSame(afterFault, afterStop);
            await afterStop.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(3, platform.RunCount);
            Assert.Equal(TrayServiceLifecycleState.Ready, service.LifecycleState);
        }
        finally
        {
            await service.StopAsync();
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConcurrentStartCallsShareTheSameReadinessTaskAndRun()
    {
        var platform = new ControlledTrayNativePlatform { BlockAdd = true };
        var service = CreateService(platform);
        try
        {
            var first = service.StartAsync();
            await platform.IconAddStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var second = service.StartAsync();
            Assert.Same(first, second);
            Assert.Equal(1, platform.RunCount);

            platform.ContinueIconAdd.Set();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            platform.ContinueIconAdd.Set();
            await service.StopAsync();
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConcreteTrayCallbackEventsRaiseExpectedActions()
    {
        var platform = new ControlledTrayNativePlatform();
        var service = CreateService(
            platform,
            clickDelay: new ImmediateClickDelay(),
            doubleClickTimeProvider: new FixedDoubleClickTimeProvider());
        var actions = new ConcurrentQueue<TrayAction>();
        service.ActionRaised += (_, action) => actions.Enqueue(action);
        try
        {
            await service.StartAsync().WaitAsync(TimeSpan.FromSeconds(3));

            await platform.SendWindowEventAsync(TrayWindowEventKind.LeftButtonUp);
            await platform.SendWindowEventAsync(TrayWindowEventKind.ContextMenu);
            await platform.SendWindowEventAsync(TrayWindowEventKind.LeftButtonDoubleClick);
            await platform.SendWindowEventAsync(TrayWindowEventKind.KeyboardActivate);

            Assert.Equal(
                [TrayAction.TogglePopup, TrayAction.ShowMenu, TrayAction.ShowPopup, TrayAction.TogglePopup],
                actions.ToArray());
        }
        finally
        {
            await service.StopAsync();
            await service.DisposeAsync();
        }
    }

    private static TrayService CreateService(
        ControlledTrayNativePlatform platform,
        ManualTimeProvider? clock = null,
        RecordingLog? log = null,
        IClickDelay? clickDelay = null,
        IDoubleClickTimeProvider? doubleClickTimeProvider = null) =>
        new(
            log ?? new RecordingLog(),
            platform,
            clock ?? new ManualTimeProvider(DateTimeOffset.UnixEpoch),
            clickDelay,
            doubleClickTimeProvider);

    private static void AssertOrdered(IReadOnlyList<string> calls, params string[] expected)
    {
        var previous = -1;
        foreach (var call in expected)
        {
            var next = calls.ToList().FindIndex(previous + 1, candidate => candidate == call);
            Assert.True(next > previous, $"Expected '{call}' after index {previous}. Calls: {string.Join(", ", calls)}");
            previous = next;
        }
    }

    private sealed class RecordingLog : IBatterioLog
    {
        private readonly ConcurrentQueue<(BatterioLogLevel Level, string Message, Exception? Exception)> _entries = new();

        public IReadOnlyList<(BatterioLogLevel Level, string Message, Exception? Exception)> Entries => _entries.ToArray();

        public void Log(BatterioLogLevel level, string message, Exception? exception = null) =>
            _entries.Enqueue((level, message, exception));
    }

    private sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private long _ticks = initial.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }

    private sealed class ImmediateClickDelay : IClickDelay
    {
        public Task Delay(TimeSpan delay, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixedDoubleClickTimeProvider : IDoubleClickTimeProvider
    {
        public TimeSpan GetDoubleClickTime() => TimeSpan.Zero;
    }

    private sealed class ControlledTrayNativePlatform : ITrayNativePlatform
    {
        private readonly BlockingCollection<QueuedMessage> _messages = new();
        private readonly ConcurrentQueue<bool> _addResults = new();
        private readonly object _callsGate = new();
        private readonly List<string> _calls = [];
        private TaskCompletionSource<bool> _callChanged = NewSignal();
        private TrayNativeWindowProcedure? _windowProcedure;
        private int _runCount;
        private int _failNextIconLoad;
        private int _failNextClassRegistration;
        private int _failNextWindowCreation;
        private int _failNextTimerCreation;
        private int _failNextMessageRetrieval;

        public bool FailNextIconLoad
        {
            get => Volatile.Read(ref _failNextIconLoad) != 0;
            set => Interlocked.Exchange(ref _failNextIconLoad, value ? 1 : 0);
        }

        public bool FailNextClassRegistration
        {
            get => Volatile.Read(ref _failNextClassRegistration) != 0;
            set => Interlocked.Exchange(ref _failNextClassRegistration, value ? 1 : 0);
        }

        public bool FailNextWindowCreation
        {
            get => Volatile.Read(ref _failNextWindowCreation) != 0;
            set => Interlocked.Exchange(ref _failNextWindowCreation, value ? 1 : 0);
        }

        public bool FailNextTimerCreation
        {
            get => Volatile.Read(ref _failNextTimerCreation) != 0;
            set => Interlocked.Exchange(ref _failNextTimerCreation, value ? 1 : 0);
        }

        public bool FailNextMessageRetrieval
        {
            get => Volatile.Read(ref _failNextMessageRetrieval) != 0;
            set => Interlocked.Exchange(ref _failNextMessageRetrieval, value ? 1 : 0);
        }

        public bool Version4SelectionSucceeds { get; set; } = true;
        public bool BlockIconLoad { get; set; }
        public bool BlockAdd { get; set; }
        public ManualResetEventSlim ContinueIconLoad { get; } = new(false);
        public ManualResetEventSlim ContinueIconAdd { get; } = new(false);
        public TaskCompletionSource<bool> IconLoadStarted { get; } = NewSignal();
        public TaskCompletionSource<bool> IconAddStarted { get; } = NewSignal();
        public int RunCount => Volatile.Read(ref _runCount);

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_callsGate)
                {
                    return _calls.ToArray();
                }
            }
        }

        public void EnqueueAddResults(params bool[] outcomes)
        {
            foreach (var outcome in outcomes)
            {
                _addResults.Enqueue(outcome);
            }
        }

        public int CountCalls(string exactCall) => Calls.Count(call => call == exactCall);

        public async Task WaitForCallAsync(string exactCall, int occurrence = 1)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (true)
            {
                Task changed;
                lock (_callsGate)
                {
                    if (_calls.Count(call => call == exactCall) >= occurrence)
                    {
                        return;
                    }

                    changed = _callChanged.Task;
                }

                await changed.WaitAsync(timeout.Token);
            }
        }

        public Task SendWindowEventAsync(TrayWindowEventKind kind)
        {
            var message = new QueuedMessage(1, new TrayWindowEvent(kind, 30));
            _messages.Add(message);
            return message.Dispatched.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }

        public nint GetModuleHandle() => 10;

        public nint LoadIcon(string path)
        {
            Record("LoadIcon");
            Interlocked.Increment(ref _runCount);
            IconLoadStarted.TrySetResult(true);
            if (BlockIconLoad)
            {
                ContinueIconLoad.Wait(TimeSpan.FromSeconds(3));
            }

            return Interlocked.Exchange(ref _failNextIconLoad, 0) == 1 ? 0 : 20;
        }

        public uint RegisterTaskbarCreatedMessage()
        {
            Record("RegisterTaskbarCreatedMessage");
            return 0xC001;
        }

        public ushort RegisterWindowClass(
            string className,
            nint instance,
            nint icon,
            uint taskbarCreatedMessage,
            TrayNativeWindowProcedure windowProcedure)
        {
            Record("RegisterClass");
            if (Interlocked.Exchange(ref _failNextClassRegistration, 0) == 1)
            {
                return 0;
            }

            _windowProcedure = windowProcedure;
            return 1;
        }

        public nint CreateCallbackWindow(string className, nint instance)
        {
            Record("CreateWindow");
            return Interlocked.Exchange(ref _failNextWindowCreation, 0) == 1 ? 0 : 30;
        }

        public int GetMessage(out object message)
        {
            if (Interlocked.Exchange(ref _failNextMessageRetrieval, 0) == 1)
            {
                message = new QueuedMessage(-1, null);
                return -1;
            }

            var next = _messages.Take();
            message = next;
            return next.Result;
        }

        public bool TranslateMessage(object message) => true;

        public nint DispatchMessage(object message)
        {
            var queued = (QueuedMessage)message;
            if (queued.Event is { } windowEvent)
            {
                _windowProcedure?.Invoke(30, windowEvent with { Window = 30 });
                queued.Dispatched.TrySetResult(true);
            }

            return 0;
        }

        public bool TryAddIcon(nint window, nint icon, out bool version4)
        {
            Record("NIM_ADD");
            IconAddStarted.TrySetResult(true);
            if (BlockAdd)
            {
                ContinueIconAdd.Wait(TimeSpan.FromSeconds(3));
            }

            var added = _addResults.TryDequeue(out var outcome) ? outcome : true;
            version4 = false;
            if (added)
            {
                Record("NIM_SETVERSION");
                version4 = Version4SelectionSucceeds;
            }

            return added;
        }

        public bool DeleteIcon(nint window)
        {
            Record("NIM_DELETE");
            return true;
        }

        public nuint StartRetryTimer(nint window, nuint timerId, uint intervalMilliseconds)
        {
            Record($"SetTimer:{intervalMilliseconds}");
            return Interlocked.Exchange(ref _failNextTimerCreation, 0) == 1 ? 0 : timerId;
        }

        public bool CancelRetryTimer(nint window, nuint timerId)
        {
            Record("KillTimer");
            return true;
        }

        public bool PostClose(nint window)
        {
            Record("PostClose");
            _messages.Add(new QueuedMessage(1, new TrayWindowEvent(TrayWindowEventKind.Close, window)));
            return true;
        }

        public void PostQuitMessage(int exitCode)
        {
            Record("PostQuit");
            _messages.Add(new QueuedMessage(0, null));
        }

        public bool DestroyWindow(nint window)
        {
            Record("DestroyWindow");
            return true;
        }

        public bool UnregisterWindowClass(string className, nint instance)
        {
            Record("UnregisterClass");
            return true;
        }

        public bool DestroyIcon(nint icon)
        {
            Record("DestroyIcon");
            return true;
        }

        public Batterio.Core.Abstractions.TrayIconPlacement? GetIconPlacement(nint window) => null;

        private void Record(string call)
        {
            TaskCompletionSource<bool> signal;
            lock (_callsGate)
            {
                _calls.Add(call);
                signal = _callChanged;
                _callChanged = NewSignal();
            }

            signal.TrySetResult(true);
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class QueuedMessage(int result, TrayWindowEvent? windowEvent)
        {
            public int Result { get; } = result;
            public TrayWindowEvent? Event { get; } = windowEvent;
            public TaskCompletionSource<bool> Dispatched { get; } = NewSignal();
        }
    }
}

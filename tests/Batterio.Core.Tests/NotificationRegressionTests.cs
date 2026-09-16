using System.Collections.Concurrent;
using System.Text.Json;
using Batterio.Core;
using Batterio.Core.Abstractions;
using Batterio.Core.Notifications;

namespace Batterio.Core.Tests;

public sealed class NotificationRegressionTests
{
    [Theory]
    [InlineData(0, BatteryChargeState.NotCharging, true)]
    [InlineData(29, BatteryChargeState.NotCharging, true)]
    [InlineData(29, BatteryChargeState.Charging, true)]
    [InlineData(30, BatteryChargeState.NotCharging, false)]
    [InlineData(31, BatteryChargeState.NotCharging, false)]
    [InlineData(null, BatteryChargeState.NotCharging, false)]
    [InlineData(null, BatteryChargeState.Offline, false)]
    public async Task ReadingBoundariesAreObservedBeforeAsserting(int? percentage, BatteryChargeState state, bool expected)
    {
        await using var fixture = await Fixture.Start();
        fixture.Factory.Read = (_, _) => Task.FromResult(new BatteryReading(percentage, state));
        await fixture.Polling.RefreshDeviceAsync("one");
        await fixture.Barrier();
        Assert.Equal(expected ? 1 : 0, fixture.Notifications.Calls.Count(id => id == "one"));
    }

    [Fact]
    public async Task FirstReadingAlreadyLowIsNotMissed()
    {
        await using var fixture = await Fixture.Start(firstReading: 29);
        await fixture.Barrier();
        Assert.Single(fixture.Notifications.Calls, id => id == "one");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalOrDeselectionAndReselectionPreserveSuppression(bool deselection)
    {
        await using var fixture = await Fixture.Start(firstReading: 29);
        await fixture.Barrier();
        await fixture.Polling.SetActiveDevicesAsync(deselection ? [Device("one", false)] : []);
        await fixture.Polling.SetActiveDevicesAsync([Device("ONE")]);
        await fixture.Polling.RefreshDeviceAsync("ONE");
        await fixture.Barrier();
        Assert.Single(fixture.Notifications.Calls, id => id.Equals("one", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OfflineAndRecoveryDoNotRearmNotification()
    {
        await using var fixture = await Fixture.Start(firstReading: 29);
        foreach (var reading in new[] { BatteryReading.Offline, new BatteryReading(80, BatteryChargeState.Charging), new BatteryReading(5, BatteryChargeState.NotCharging) })
        {
            fixture.Factory.Read = (_, _) => Task.FromResult(reading);
            await fixture.Polling.RefreshDeviceAsync("one");
        }
        await fixture.Barrier();
        Assert.Single(fixture.Notifications.Calls, id => id == "one");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledAdapterCannotTriggerOrConsumeSuppression(bool cancelled)
    {
        await using var fixture = await Fixture.Start();
        fixture.Factory.Read = (_, _) => Task.FromException<BatteryReading>(cancelled
            ? new OperationCanceledException() : new InvalidOperationException("read failed"));
        await fixture.Polling.RefreshDeviceAsync("one");
        await fixture.Barrier();
        Assert.DoesNotContain("one", fixture.Notifications.Calls);
        Assert.NotNull(fixture.Polling.States.Single(s => s.Configuration.Id == "one").Error);
        fixture.Factory.Read = (_, _) => Task.FromResult(new BatteryReading(5, BatteryChargeState.Charging));
        await fixture.Polling.RefreshDeviceAsync("one");
        await fixture.Barrier();
        Assert.Single(fixture.Notifications.Calls, id => id == "one");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateLowResultAfterTimeoutOrRetirementNeverNotifies(bool retired)
    {
        await using var fixture = await Fixture.Start();
        var started = Signal();
        var timedOut = Signal();
        fixture.Polling.StateChanged += (_, args) => { if (args.State.Error is not null) timedOut.TrySetResult(); };
        var result = new TaskCompletionSource<BatteryReading>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Factory.Read = (_, _) => { started.TrySetResult(); return result.Task; };
        var refresh = fixture.Polling.RefreshDeviceAsync("one");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            if (retired) await fixture.Polling.SetActiveDevicesAsync([]);
            if (!retired)
            {
                await timedOut.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.NotNull(fixture.Polling.States.Single().Error);
            }
        }
        finally { result.TrySetResult(new BatteryReading(5, BatteryChargeState.NotCharging)); }
        try { await refresh.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) when (retired) { }
        // Drain worker retirement before checking the policy, not just a timing-based delay.
        await fixture.Polling.SetActiveDevicesAsync([]);
        await fixture.Factory.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Barrier();
        Assert.DoesNotContain("one", fixture.Notifications.Calls);
    }

    [Fact]
    public async Task ConcurrentDevicesAndRefreshesDeliverOncePerId()
    {
        await using var fixture = await Fixture.Start();
        fixture.Factory.Read = (_, _) => Task.FromResult(new BatteryReading(5, BatteryChargeState.NotCharging));
        var devices = Enumerable.Range(0, 12).Select(i => Device("device" + i)).ToArray();
        await fixture.Polling.SetActiveDevicesAsync(devices);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => fixture.Polling.RefreshAllAsync()));
        await fixture.Barrier();
        var calls = fixture.Notifications.Calls.Where(id => id.StartsWith("device", StringComparison.Ordinal)).ToArray();
        Assert.Equal(12, calls.Length);
        Assert.Equal(12, calls.Distinct().Count());
    }

    [Fact]
    public async Task ShutdownCancelsDeliveryDropsQueueAndDisposesOnlyOnce()
    {
        await using var fixture = await Fixture.Start();
        var started = Signal();
        var cancelled = Signal();
        fixture.Notifications.Deliver = async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        };
        fixture.Factory.Read = (_, _) => Task.FromResult(new BatteryReading(5, BatteryChargeState.NotCharging));
        await fixture.Polling.RefreshDeviceAsync("one");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Polling.SetActiveDevicesAsync([Device("one"), Device("queued")]);
        await fixture.Polling.RefreshAllAsync();
        await Task.WhenAll(fixture.Coordinator.StopAsync(), fixture.Coordinator.StopAsync());
        await fixture.Coordinator.DisposeAsync();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(fixture.Notifications.Calls);
        Assert.Equal(1, fixture.Notifications.DisposeCount);
        await fixture.Polling.RefreshAllAsync();
        Assert.Single(fixture.Notifications.Calls);
    }

    [Fact]
    public async Task InitializationFailureLeavesPollingWorkingAndIsCleanedUp()
    {
        await using var fixture = await Fixture.Start(initializationFails: true, firstReading: 5);
        Assert.Equal(5, fixture.Polling.States.Single().Percentage);
        Assert.Null(fixture.Polling.States.Single().Error);
        Assert.Empty(fixture.Notifications.Calls);
        await fixture.Coordinator.StopAsync();
        Assert.Equal(1, fixture.Notifications.DisposeCount);
    }

    [Fact]
    public async Task UnexpectedDeliveryCancellationDoesNotKillConsumer()
    {
        await using var fixture = await Fixture.Start();
        fixture.Notifications.Deliver = (id, _) => id == "one" ? Task.FromException(new OperationCanceledException()) : Task.CompletedTask;
        fixture.Factory.Read = (_, _) => Task.FromResult(new BatteryReading(5, BatteryChargeState.NotCharging));
        await fixture.Polling.RefreshDeviceAsync("one");
        await fixture.Polling.RefreshDeviceAsync("one");
        await fixture.Barrier();
        Assert.Single(fixture.Notifications.Calls, id => id == "one");
        Assert.Null(fixture.Polling.States.Single(s => s.Configuration.Id == "one").Error);
    }

    [Fact]
    public async Task StopWaitsForInitializationAndNeverSubscribesAfterShutdown()
    {
        var initialization = Signal();
        var notifications = new RecordingService { Initialize = _ => initialization.Task };
        var factory = new ScriptFactory();
        await using var polling = new PollingCoordinator(new AdapterRegistry([factory]));
        await using var coordinator = new LowBatteryNotificationCoordinator(polling, notifications);
        var start = coordinator.StartAsync();
        var stop = coordinator.StopAsync();
        Assert.False(stop.IsCompleted);
        Assert.Equal(0, notifications.DisposeCount);
        initialization.SetResult();
        await Task.WhenAll(start, stop).WaitAsync(TimeSpan.FromSeconds(5));
        factory.Read = (_, _) => Task.FromResult(new BatteryReading(5, BatteryChargeState.NotCharging));
        await polling.SetActiveDevicesAsync([Device("one")]);
        await polling.RefreshAllAsync();
        Assert.Empty(notifications.Calls);
        Assert.Equal(1, notifications.DisposeCount);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static DeviceConfiguration Device(string id, bool enabled = true) => new()
    {
        Id = id, Name = "Same name", Enabled = enabled, AdapterId = "script",
        AdapterSettings = JsonDocument.Parse("{}").RootElement, PollIntervalSeconds = 300, TimeoutSeconds = 1
    };

    private sealed class Fixture : IAsyncDisposable
    {
        public ScriptFactory Factory { get; } = new();
        public RecordingService Notifications { get; } = new();
        public PollingCoordinator Polling { get; }
        public LowBatteryNotificationCoordinator Coordinator { get; }
        private int _barrier;
        private Fixture()
        {
            Polling = new(new AdapterRegistry([Factory]));
            Coordinator = new(Polling, Notifications);
        }
        public static async Task<Fixture> Start(int firstReading = 80, bool initializationFails = false)
        {
            var fixture = new Fixture();
            fixture.Factory.Read = (_, _) => Task.FromResult(new BatteryReading(firstReading, BatteryChargeState.NotCharging));
            fixture.Notifications.InitializationFails = initializationFails;
            await fixture.Coordinator.StartAsync();
            await fixture.Polling.SetActiveDevicesAsync([Device("one")]);
            await fixture.Polling.RefreshAllAsync();
            return fixture;
        }
        public async Task Barrier()
        {
            // A known low sentinel passes through the same FIFO consumer after preceding state events.
            var id = "barrier" + ++_barrier;
            var completion = Notifications.Completion(id);
            await Polling.SetActiveDevicesAsync([.. Polling.States.Select(s => s.Configuration), Device(id)]);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            await Polling.DisposeAsync();
        }
    }
    private sealed class ScriptFactory : IBatteryAdapterFactory
    {
        public Func<string, CancellationToken, Task<BatteryReading>> Read = (_, _) => Task.FromResult(new BatteryReading(80, BatteryChargeState.NotCharging));
        public TaskCompletionSource Disposed { get; } = Signal();
        public string AdapterId => "script";
        public void ValidateSettings(JsonElement settings) { }
        public IBatteryAdapter CreateAdapter(DeviceConfiguration device) => new ScriptAdapter(this, device.Id);
    }
    private sealed class ScriptAdapter(ScriptFactory factory, string id) : IBatteryAdapter
    {
        public Task<BatteryReading> GetBatteryStatus(CancellationToken cancellationToken = default) =>
            id.StartsWith("barrier", StringComparison.Ordinal)
                ? Task.FromResult(new BatteryReading(5, BatteryChargeState.NotCharging)) : factory.Read(id, cancellationToken);
        public ValueTask DisposeAsync() { factory.Disposed.TrySetResult(); return ValueTask.CompletedTask; }
    }
    private sealed class RecordingService : IBatteryNotificationService
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _completed = new();
        public bool InitializationFails;
        public int DisposeCount;
        public Func<string, CancellationToken, Task> Deliver = (_, _) => Task.CompletedTask;
        public Func<CancellationToken, Task> Initialize = _ => Task.CompletedTask;
        public TaskCompletionSource Completion(string id) => _completed.GetOrAdd(id, _ => Signal());
        public Task InitializeAsync(CancellationToken cancellationToken = default) => InitializationFails
            ? Task.FromException(new InvalidOperationException("registration failed")) : Initialize(cancellationToken);
        public async Task ShowLowBatteryAsync(string deviceId, string deviceName, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue(deviceId);
            await Deliver(deviceId, cancellationToken);
            Completion(deviceId).TrySetResult();
        }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref DisposeCount); return ValueTask.CompletedTask; }
    }
}

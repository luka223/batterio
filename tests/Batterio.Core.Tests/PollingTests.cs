using System.Text.Json;
using Batterio.Core;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

public sealed partial class PollingTests
{
    [Fact]
    public async Task SuccessfulZeroAndHundredReadingsArePublished()
    {
        var factory = new SequenceFactory(0, 100);
        await using var coordinator = CreateCoordinator(factory);
        await coordinator.ApplyConfigurationAsync(Snapshot(Device("one")));

        await Eventually(() => coordinator.States.Single().State != BatteryChargeState.Offline && coordinator.States.Single().Percentage == 0);
        await coordinator.RefreshAllAsync();
        await Eventually(() => coordinator.States.Single().Percentage == 100);
    }

    [Fact]
    public async Task InvalidValuesAndExceptionsBecomeUnavailableWithoutFabricatingZero()
    {
        var factory = new SequenceFactory(-1, 101, new InvalidOperationException("offline"));
        await using var coordinator = CreateCoordinator(factory);
        await coordinator.ApplyConfigurationAsync(Snapshot(Device("one")));

        await Eventually(() => coordinator.States.Single().State == BatteryChargeState.Offline);
        var state = coordinator.States.Single();
        Assert.Null(state.Percentage);
        Assert.Equal(BatteryChargeState.Offline, state.State);
    }

    [Fact]
    public async Task ATimeoutClearsTheReadingAndDoesNotOverlap()
    {
        var adapter = new FirstThenBlockingAdapter(75);
        await using var coordinator = CreateCoordinator(new SingleAdapterFactory(adapter));
        await coordinator.ApplyConfigurationAsync(Snapshot(Device("one")));

        await Eventually(() => coordinator.States.Single().Percentage == 75);
        await Task.Delay(250);
        var refresh = coordinator.RefreshAllAsync();
        await Task.Delay(100);
        Assert.True(adapter.Started);
        Assert.Equal(75, coordinator.States.Single().Percentage);
        await Eventually(() => coordinator.States.Single().State == BatteryChargeState.Offline);
        var state = coordinator.States.Single();
        Assert.Null(state.Percentage);
        Assert.Equal(BatteryChargeState.Offline, state.State);

        Assert.False(refresh.IsCompleted);
        adapter.Complete(75);
        await refresh;
    }

    [Fact]
    public async Task PartialReadingReplacesTheWholePreviousReading()
    {
        var factory = new SequenceFactory(new BatteryReading(15, BatteryChargeState.Charging), new BatteryReading(null, BatteryChargeState.Charging));
        await using var coordinator = CreateCoordinator(factory);
        await coordinator.ApplyConfigurationAsync(Snapshot(Device("one")));

        await Eventually(() => coordinator.States.Single().Percentage == 15);
        await coordinator.RefreshAllAsync();
        await Eventually(() => coordinator.States.Single().Percentage is null);

        var state = coordinator.States.Single();
        Assert.Null(state.Percentage);
        Assert.Equal(BatteryChargeState.Charging, state.State);
    }

    [Fact]
    public async Task RetiredAdapterCannotPublishLateReading()
    {
        var oldAdapter = new BlockingAdapter();
        var factory = new QueueFactory(oldAdapter, new ImmediateAdapter(80));
        await using var coordinator = CreateCoordinator(factory);
        await coordinator.ApplyConfigurationAsync(Snapshot(Device("same")));
        await Eventually(() => oldAdapter.Started);

        var replacement = coordinator.ApplyConfigurationAsync(Snapshot(Device("same")));
        await Task.Delay(100);
        Assert.False(replacement.IsCompleted);
        oldAdapter.Complete(5);
        await replacement;
        await Eventually(() => coordinator.States.Single().Percentage == 80);
        await Eventually(() => oldAdapter.Disposed);

        Assert.Equal(80, coordinator.States.Single().Percentage);
    }

    [Fact]
    public async Task GlobalConcurrencyIsLimitedToFour()
    {
        var adapters = Enumerable.Range(0, 8).Select(_ => new BlockingAdapter()).ToArray();
        var factory = new QueueFactory(adapters);
        await using var coordinator = CreateCoordinator(factory);
        await coordinator.ApplyConfigurationAsync(new ConfigurationSnapshot(
            "devices.json",
            "",
            adapters.Select((_, index) => Device($"device-{index}")).ToArray()));

        await Eventually(() => adapters.Count(adapter => adapter.Started) == 4);
        Assert.Equal(4, adapters.Count(adapter => adapter.Started));
        foreach (var adapter in adapters)
        {
            adapter.Complete(50);
        }
    }

    private static PollingCoordinator CreateCoordinator(IBatteryAdapterFactory factory, int timeoutSeconds = 1) =>
        new(new AdapterRegistry(new[] { factory }));

    private static ConfigurationSnapshot Snapshot(params DeviceConfiguration[] devices) =>
        new("devices.json", "", devices.Select(device => new DeviceConfiguration
        {
            Id = device.Id,
            Name = device.Name,
            AdapterId = device.AdapterId,
            AdapterSettings = device.AdapterSettings,
            Enabled = device.Enabled,
            PollIntervalSeconds = device.PollIntervalSeconds,
            TimeoutSeconds = Math.Min(device.TimeoutSeconds, device.PollIntervalSeconds)
        }).ToArray());

    private static DeviceConfiguration Device(string id) => new()
    {
        Id = id,
        Name = id,
        AdapterId = "test",
        AdapterSettings = JsonDocument.Parse("{}").RootElement,
        PollIntervalSeconds = 5,
        TimeoutSeconds = 1
    };

    private static async Task Eventually(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(condition(), "Condition was not reached before the timeout.");
    }

    private sealed class SequenceFactory(params object[] values) : IBatteryAdapterFactory
    {
        private readonly Queue<object> _values = new(values);
        public string AdapterId => "test";
        public void ValidateSettings(JsonElement settings) { }
        public IBatteryAdapter CreateAdapter(DeviceConfiguration device) => new SequenceAdapter(_values);
    }

    private sealed class SequenceAdapter(Queue<object> values) : IBatteryAdapter
    {
        public Task<BatteryReading> GetBatteryStatus(CancellationToken cancellationToken = default)
        {
            var next = values.Count == 0 ? 50 : values.Dequeue();
            return next switch
            {
                int value => Task.FromResult(new BatteryReading(value, BatteryChargeState.NotCharging)),
                BatteryReading reading => Task.FromResult(reading),
                Exception exception => Task.FromException<BatteryReading>(exception),
                _ => Task.FromResult(new BatteryReading(50, BatteryChargeState.NotCharging))
            };
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SingleAdapterFactory(IBatteryAdapter adapter) : IBatteryAdapterFactory
    {
        public string AdapterId => "test";
        public void ValidateSettings(JsonElement settings) { }
        public IBatteryAdapter CreateAdapter(DeviceConfiguration device) => adapter;
    }

    private sealed class QueueFactory(params IBatteryAdapter[] adapters) : IBatteryAdapterFactory
    {
        private readonly Queue<IBatteryAdapter> _adapters = new(adapters);
        public string AdapterId => "test";
        public void ValidateSettings(JsonElement settings) { }
        public IBatteryAdapter CreateAdapter(DeviceConfiguration device)
        {
            return _adapters.Dequeue();
        }
    }

    private sealed class ImmediateAdapter(int value) : IBatteryAdapter
    {
        public Task<BatteryReading> GetBatteryStatus(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BatteryReading(value, BatteryChargeState.NotCharging));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingAdapter : IBatteryAdapter
    {
        private readonly TaskCompletionSource<BatteryReading> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public Task<BatteryReading> GetBatteryStatus(CancellationToken cancellationToken = default)
        {
            Started = true;
            return _completion.Task;
        }
        public void Complete(int result) => _completion.TrySetResult(new BatteryReading(result, BatteryChargeState.NotCharging));
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FirstThenBlockingAdapter(int firstValue) : IBatteryAdapter
    {
        private readonly TaskCompletionSource<BatteryReading> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public bool Started { get; private set; }

        public Task<BatteryReading> GetBatteryStatus(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                return Task.FromResult(new BatteryReading(firstValue, BatteryChargeState.NotCharging));
            }

            Started = true;
            return _completion.Task;
        }

        public void Complete(int result) => _completion.TrySetResult(new BatteryReading(result, BatteryChargeState.NotCharging));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

}

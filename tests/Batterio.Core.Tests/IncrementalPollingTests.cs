using System.Text.Json;
using Batterio.Core;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

public sealed class IncrementalPollingTests
{
    [Fact]
    public async Task UnchangedConfigurationKeepsTheExistingWorker()
    {
        var factory = new QueueFactory(new ImmediateAdapter(20), new ImmediateAdapter(80));
        await using var coordinator = new PollingCoordinator(new AdapterRegistry(new[] { factory }));
        var device = Device("mouse");

        await coordinator.SetActiveDevicesAsync(new[] { device });
        await Eventually(() => coordinator.States.Single().Percentage == 20);
        await coordinator.SetActiveDevicesAsync(new[] { device });

        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(20, coordinator.States.Single().Percentage);
    }

    [Fact]
    public async Task RemovingTheLastDevicePublishesAnEmptySnapshotAndDiscardsLateResults()
    {
        var adapter = new BlockingAdapter();
        await using var coordinator = new PollingCoordinator(new AdapterRegistry(new[] { new QueueFactory(adapter) }));
        var snapshots = new List<PollingSnapshot>();
        coordinator.SnapshotChanged += (_, args) => snapshots.Add(args.Snapshot);
        var device = Device("mouse");

        await coordinator.SetActiveDevicesAsync(new[] { device });
        await Eventually(() => adapter.Started);
        await coordinator.SetActiveDevicesAsync(Array.Empty<DeviceConfiguration>());

        Assert.Empty(coordinator.States);
        Assert.NotEmpty(snapshots);
        Assert.Empty(snapshots[^1].States);
        adapter.Complete(5);
        await Eventually(() => adapter.Disposed);
        Assert.Empty(coordinator.States);
    }

    [Fact]
    public async Task ReenableWaitsForRetirementBeforeCreatingReplacement()
    {
        var oldAdapter = new BlockingAdapter();
        var replacement = new ImmediateAdapter(80);
        var factory = new QueueFactory(oldAdapter, replacement);
        await using var coordinator = new PollingCoordinator(new AdapterRegistry(new[] { factory }));
        var device = Device("mouse");

        await coordinator.SetActiveDevicesAsync(new[] { device });
        await Eventually(() => oldAdapter.Started);
        await coordinator.SetActiveDevicesAsync(Array.Empty<DeviceConfiguration>());

        var reenable = coordinator.SetActiveDevicesAsync(new[] { device });
        await Task.Delay(100);
        Assert.False(reenable.IsCompleted);
        Assert.Equal(1, factory.CreateCount);

        oldAdapter.Complete(5);
        await reenable;
        await Eventually(() => coordinator.States.Single().Percentage == 80);
        Assert.True(oldAdapter.Disposed);
        Assert.Equal(2, factory.CreateCount);
    }

    private static DeviceConfiguration Device(string id) => new()
    {
        Id = id,
        Name = id,
        AdapterId = "test",
        PollIntervalSeconds = 300,
        TimeoutSeconds = 10,
        AdapterSettings = JsonDocument.Parse("{}").RootElement
    };

    private static async Task Eventually(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private sealed class QueueFactory(params IBatteryAdapter[] adapters) : IBatteryAdapterFactory
    {
        private readonly Queue<IBatteryAdapter> _adapters = new(adapters);

        public string AdapterId => "test";

        public int CreateCount { get; private set; }

        public void ValidateSettings(JsonElement settings) { }

        public IBatteryAdapter CreateAdapter(DeviceConfiguration device)
        {
            CreateCount++;
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

        public void Complete(int percentage) =>
            _completion.TrySetResult(new BatteryReading(percentage, BatteryChargeState.NotCharging));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

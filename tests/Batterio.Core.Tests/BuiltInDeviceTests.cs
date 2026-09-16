using System.Text.Json;
using Batterio.Core;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

public sealed class BuiltInDeviceTests
{
    [Fact]
    public async Task BuiltInDevicesContainOnlyTheTwoLogitechAdapters()
    {
        var devices = BuiltInDevices.Create();
        Assert.Equal(2, devices.Count);
        Assert.All(devices, device => Assert.Equal(300, device.PollIntervalSeconds));
        Assert.Equal(
            new[] { "logitech-g-pro-x2-superstrike", "logitech-g-pro-x2-lightspeed" },
            devices.Select(device => device.AdapterId));

        await using var coordinator = new PollingCoordinator(new AdapterRegistry([
            new FixedFactory("logitech-g-pro-x2-superstrike", new BatteryReading(64, BatteryChargeState.NotCharging)),
            new FixedFactory("logitech-g-pro-x2-lightspeed", new BatteryReading(15, BatteryChargeState.Charging))]));
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readings = 0;
        coordinator.StateChanged += (_, e) =>
        {
            if (e.State.Percentage is not null && Interlocked.Increment(ref readings) == 2)
            {
                ready.TrySetResult(true);
            }
        };

        await coordinator.StartDevicesAsync(devices);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Logitech G Pro X2 Superstrike", coordinator.States.Single(state => state.Configuration.Id == "logitech-g-pro-x2-superstrike").Configuration.Name);
        Assert.Equal("Logitech G Pro X2 Lightspeed", coordinator.States.Single(state => state.Configuration.Id == "logitech-g-pro-x2-lightspeed").Configuration.Name);
        Assert.Equal(64, coordinator.States.Single(state => state.Configuration.Id == "logitech-g-pro-x2-superstrike").Percentage);
        Assert.Equal(15, coordinator.States.Single(state => state.Configuration.Id == "logitech-g-pro-x2-lightspeed").Percentage);
        Assert.Equal(BatteryChargeState.Charging, coordinator.States.Single(state => state.Configuration.Id == "logitech-g-pro-x2-lightspeed").State);

        await coordinator.RefreshAllAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.RefreshDeviceAsync(devices[0].Id).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(5, readings);
    }

    [Fact]
    public async Task PollingRepeatsWithoutManualRefresh()
    {
        await using var coordinator = new PollingCoordinator(new AdapterRegistry([
            new SequenceFactory("test", new BatteryReading(64, BatteryChargeState.NotCharging))]));
        var repeated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readings = 0;
        coordinator.StateChanged += (_, e) =>
        {
            if (e.State.Percentage is not null && Interlocked.Increment(ref readings) == 2)
            {
                repeated.TrySetResult(true);
            }
        };

        await coordinator.StartDevicesAsync([new DeviceConfiguration
        {
            Id = "test-device", Name = "Test device", AdapterId = "test", PollIntervalSeconds = 1
        }]);
        await repeated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(64, coordinator.States.Single().Percentage);
    }

    [Fact]
    public async Task TestAdaptersHonorCancellation()
    {
        await using var superstrike = new LogitechGProX2SuperstrikeAdapter();
        await using var lightspeed = new LogitechGProX2LightspeedAdapter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => superstrike.GetBatteryStatus(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lightspeed.GetBatteryStatus(cancellation.Token));
    }

    private sealed class FixedFactory(string adapterId, BatteryReading reading) : IBatteryAdapterFactory
    {
        public string AdapterId { get; } = adapterId;

        public void ValidateSettings(JsonElement settings) { }

        public IBatteryAdapter CreateAdapter(DeviceConfiguration device) => new FixedAdapter(reading);
    }

    private sealed class SequenceFactory(string adapterId, BatteryReading reading) : IBatteryAdapterFactory
    {
        public string AdapterId { get; } = adapterId;

        public void ValidateSettings(JsonElement settings) { }

        public IBatteryAdapter CreateAdapter(DeviceConfiguration device) => new FixedAdapter(reading);
    }

    private sealed class FixedAdapter(BatteryReading reading) : IBatteryAdapter
    {
        public Task<BatteryReading> GetBatteryStatus(CancellationToken cancellationToken = default) =>
            Task.FromResult(reading);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

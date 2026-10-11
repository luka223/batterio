using System.Text.Json;
using Batterio.Core;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

public sealed class BuiltInDeviceTests
{
    [Fact]
    public void SuperstrikePresenceQueriesPreferWiredUsbAndLightspeedKeepsItsReceiverQuery()
    {
        var superstrike = BuiltInDevices.Catalog.Single(device => device.Id == "logitech-g-pro-x2-superstrike");
        var wired = superstrike.PresenceQueries[0];
        var receiver = superstrike.PresenceQueries[1];
        var lightspeed = BuiltInDevices.Catalog.Single(device => device.Id == "logitech-g-pro-x2-lightspeed");

        Assert.Equal(2, superstrike.PresenceQueries.Count);
        Assert.Equal((ushort)0x046D, wired.VendorId);
        Assert.Equal((ushort)0xC0A8, wired.ProductId);
        Assert.Equal((ushort)2, wired.InterfaceNumber);
        Assert.Equal((ushort)0xFF00, wired.UsagePage);
        Assert.Equal((ushort)0x0002, wired.Usage);
        Assert.Equal(20, wired.MinimumReportLength);
        Assert.Equal((byte)0x11, wired.RequiredReportId);
        Assert.Equal((ushort)0xC54D, receiver.ProductId);
        Assert.Single(lightspeed.PresenceQueries);
        Assert.Same(LogitechReceiverDefinitions.LightspeedQuery, lightspeed.PresenceQueries[0]);
    }

    [Fact]
    public async Task BuiltInDevicesContainTheShippedAdapters()
    {
        var devices = BuiltInDevices.Create();
        Assert.Equal(5, devices.Count);
        Assert.All(devices, device => Assert.Equal(300, device.PollIntervalSeconds));
        Assert.Equal(
            new[]
            {
                "logitech-g-pro-x2-superstrike", "logitech-g-pro-x2-lightspeed", "steelseries-arctis-nova-pro-omni",
                "logitech-mx-master-3s", "logitech-mx-keys-mechanical"
            },
            devices.Select(device => device.AdapterId));

        await using var coordinator = new PollingCoordinator(new AdapterRegistry([
            new FixedFactory("logitech-g-pro-x2-superstrike", new BatteryReading(64, BatteryChargeState.NotCharging)),
            new FixedFactory("logitech-g-pro-x2-lightspeed", new BatteryReading(15, BatteryChargeState.Charging)),
            new FixedFactory("steelseries-arctis-nova-pro-omni", new BatteryReading(90, BatteryChargeState.NotCharging)),
            new FixedFactory("logitech-mx-master-3s", new BatteryReading(72, BatteryChargeState.NotCharging)),
            new FixedFactory("logitech-mx-keys-mechanical", new BatteryReading(41, BatteryChargeState.Charging))]));
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readings = 0;
        coordinator.StateChanged += (_, e) =>
        {
            if (e.State.Percentage is not null && Interlocked.Increment(ref readings) == 5)
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
        Assert.Equal("Logitech MX Master 3S", coordinator.States.Single(state => state.Configuration.Id == "logitech-mx-master-3s").Configuration.Name);
        Assert.Equal(41, coordinator.States.Single(state => state.Configuration.Id == "logitech-mx-keys-mechanical").Percentage);
        Assert.Equal(BatteryChargeState.Charging, coordinator.States.Single(state => state.Configuration.Id == "logitech-g-pro-x2-lightspeed").State);

        await coordinator.RefreshAllAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.RefreshDeviceAsync(devices[0].Id).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(11, readings);
    }

    [Fact]
    public void MxDevicesShareTheBoltReceiverPresenceQuery()
    {
        foreach (var id in new[] { "logitech-mx-master-3s", "logitech-mx-keys-mechanical" })
        {
            var device = BuiltInDevices.Catalog.Single(entry => entry.Id == id);
            Assert.Same(LogitechReceiverDefinitions.BoltReceiverQuery, Assert.Single(device.PresenceQueries));
        }

        var query = LogitechReceiverDefinitions.BoltReceiverQuery;
        Assert.Equal((ushort)0x046D, query.VendorId);
        Assert.Equal((ushort)0xC548, query.ProductId);
        Assert.Equal((ushort)2, query.InterfaceNumber);
        Assert.Equal((ushort)0xFF00, query.UsagePage);
        Assert.Equal(20, query.MinimumReportLength);
        Assert.Equal((byte)0x11, query.RequiredReportId);
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
        await using var omni = new SteelSeriesArctisNovaProOmniAdapter();
        await using var mxMaster = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s);
        await using var mxKeys = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxKeysMechanical);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => superstrike.GetBatteryStatus(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lightspeed.GetBatteryStatus(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => omni.GetBatteryStatus(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mxMaster.GetBatteryStatus(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mxKeys.GetBatteryStatus(cancellation.Token));
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

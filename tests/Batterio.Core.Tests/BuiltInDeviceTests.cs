using Batterio.Core;

namespace Batterio.Core.Tests;

public sealed class BuiltInDeviceTests
{
    [Fact]
    public async Task BuiltInDevicePollsAtStartupAndRefreshesImmediately()
    {
        var devices = BuiltInDevices.Create();
        Assert.Equal(3, devices.Count);
        Assert.All(devices, device => Assert.Equal(300, device.PollIntervalSeconds));
        await using var coordinator = new PollingCoordinator(
            new AdapterRegistry([
                new LogitechGProX2SuperstrikeAdapterFactory(),
                new KeychronQ1HeAdapterFactory(),
                new LogitechGProX2LightspeedAdapterFactory()]));
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readings = 0;
        coordinator.StateChanged += (_, e) =>
        {
            if (e.State.RetrievalState == BatteryRetrievalState.Ready)
            {
                Interlocked.Increment(ref readings);
                ready.TrySetResult(true);
            }
        };

        await coordinator.StartDevicesAsync(devices);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Logitech G Pro X2 Superstrike", coordinator.States.Single(state => state.Configuration.Id == "logitech-g-pro-x2-superstrike").Configuration.Name);
        Assert.Equal("Keychron Q1 HE", coordinator.States.Single(state => state.Configuration.Id == "keychron-q1-he").Configuration.Name);
        Assert.Equal("Logitech G Pro X2 Lightspeed", coordinator.States.Single(state => state.Configuration.Id == "logitech-g-pro-x2-lightspeed").Configuration.Name);
        Assert.Equal(64, coordinator.States.Single(state => state.Configuration.Id == "logitech-g-pro-x2-superstrike").LastValidPercentage);
        Assert.Equal(35, coordinator.States.Single(state => state.Configuration.Id == "keychron-q1-he").LastValidPercentage);
        Assert.Equal(15, coordinator.States.Single(state => state.Configuration.Id == "logitech-g-pro-x2-lightspeed").LastValidPercentage);
        await coordinator.RefreshAllAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.RefreshDeviceAsync(devices[0].Id).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(7, readings);
        Assert.Equal(64, coordinator.States.Single(state => state.Configuration.Id == "logitech-g-pro-x2-superstrike").LastValidPercentage);
        Assert.Equal(35, coordinator.States.Single(state => state.Configuration.Id == "keychron-q1-he").LastValidPercentage);
        Assert.Equal(15, coordinator.States.Single(state => state.Configuration.Id == "logitech-g-pro-x2-lightspeed").LastValidPercentage);
    }

    [Fact]
    public async Task PollingRepeatsWithoutManualRefresh()
    {
        await using var coordinator = new PollingCoordinator(
            new AdapterRegistry([new LogitechGProX2SuperstrikeAdapterFactory()]));
        var repeated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readings = 0;
        coordinator.StateChanged += (_, e) =>
        {
            if (e.State.RetrievalState == BatteryRetrievalState.Ready &&
                Interlocked.Increment(ref readings) == 2)
            {
                repeated.TrySetResult(true);
            }
        };

        // Exercise the real polling loop with a short interval; the production interval is checked above.
        await coordinator.StartDevicesAsync([new DeviceConfiguration
        {
            Id = "test-device", Name = "Test device", AdapterId = "logitech-g-pro-x2-superstrike", PollIntervalSeconds = 1
        }]);
        await repeated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(64, coordinator.States.Single().LastValidPercentage);
    }

    [Fact]
    public async Task TestAdapterHonorsCancellation()
    {
        await using var adapter = new LogitechGProX2SuperstrikeAdapter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetBatteryStatus(cancellation.Token));
    }
}

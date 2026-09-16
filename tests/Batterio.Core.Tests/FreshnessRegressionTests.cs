using Batterio.Core;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

public sealed partial class PollingTests
{
    [Theory]
    [InlineData(15)]
    [InlineData(null)]
    public async Task FailedReadingClearsTheDisplayAndRetryStaysOfflineUntilRecovery(int? percentage)
    {
        var retry = new TaskCompletionSource<BatteryReading>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new RetryAdapter(new BatteryReading(percentage, BatteryChargeState.Charging), retry.Task);
        await using var coordinator = CreateCoordinator(new SingleAdapterFactory(adapter));
        await coordinator.ApplyConfigurationAsync(Snapshot(Device("freshness")));
        await Eventually(() => coordinator.States.Single().State == BatteryChargeState.Charging);

        await coordinator.RefreshAllAsync();
        var failed = coordinator.States.Single();
        Assert.Equal(BatteryChargeState.Offline, failed.State);
        Assert.Null(failed.Percentage);

        var attempt = coordinator.RefreshAllAsync();
        await Eventually(() => coordinator.States.Single().State == BatteryChargeState.Offline);
        Assert.Equal(BatteryChargeState.Offline, coordinator.States.Single().State);
        Assert.Null(coordinator.States.Single().Percentage);

        retry.SetResult(new BatteryReading(16, BatteryChargeState.NotCharging));
        await attempt;
        var recovered = coordinator.States.Single();
        Assert.Equal(BatteryChargeState.NotCharging, recovered.State);
        Assert.Equal(16, recovered.Percentage);
    }

    [Theory]
    [InlineData(BatteryChargeState.Charging, true)]
    [InlineData(BatteryChargeState.NotCharging, false)]
    [InlineData(BatteryChargeState.Offline, false)]
    public void DeviceStateExposesOnlyTheCurrentReading(BatteryChargeState state, bool charging)
    {
        var reading = state == BatteryChargeState.Offline
            ? BatteryReading.Offline
            : new BatteryReading(15, state);
        var device = new DeviceBatteryState
        {
            Configuration = Device("presentation"),
            Reading = reading
        };

        Assert.Equal(state, device.State);
        Assert.Equal(reading.Percentage, device.Percentage);
        Assert.Equal(charging, device.State == BatteryChargeState.Charging);
        Assert.Same(device.Reading, device.CurrentReading);
    }

    private sealed class RetryAdapter(BatteryReading initial, Task<BatteryReading> retry) : IBatteryAdapter
    {
        private int _calls;

        public Task<BatteryReading> GetBatteryStatus(CancellationToken cancellationToken = default) => ++_calls switch
        {
            1 => Task.FromResult(initial),
            2 => Task.FromException<BatteryReading>(new IOException("receiver disconnected")),
            _ => retry.WaitAsync(cancellationToken)
        };

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

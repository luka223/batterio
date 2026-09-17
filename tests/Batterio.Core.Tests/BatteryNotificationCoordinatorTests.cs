using System.Collections.Concurrent;
using System.Text.Json;
using Batterio.Core;
using Batterio.Core.Abstractions;
using Batterio.Core.Notifications;

namespace Batterio.Core.Tests;

public sealed class BatteryNotificationCoordinatorTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(29, true)]
    [InlineData(30, false)]
    [InlineData(31, false)]
    public async Task SuccessfulReadingsBelowThirtyNotifyOnce(int percentage, bool shouldNotify)
    {
        var notifications = new RecordingNotificationService();
        await using var polling = CreatePolling(new SequenceFactory(new BatteryReading(percentage, BatteryChargeState.NotCharging)));
        await using var coordinator = new BatteryNotificationCoordinator(polling, notifications);
        await coordinator.StartAsync();
        await polling.ApplyConfigurationAsync(Snapshot(Device("one")));

        await Eventually(() => polling.States.Single().Percentage == percentage);
        await Eventually(() => notifications.Calls.Count == (shouldNotify ? 1 : 0));
        Assert.Equal(shouldNotify ? 1 : 0, notifications.Calls.Count);
        if (shouldNotify) Assert.Equal("low-battery", Assert.Single(notifications.Calls).Kind);
    }

    [Theory]
    [InlineData(BatteryChargeState.Charging)]
    [InlineData(BatteryChargeState.NotCharging)]
    public async Task InitialSuccessfulOneHundredPercentReadingNotifiesOnce(BatteryChargeState chargeState)
    {
        var notifications = new RecordingNotificationService();
        await using var polling = CreatePolling(new SequenceFactory(
            new BatteryReading(100, chargeState), new BatteryReading(100, chargeState), new BatteryReading(100, chargeState)));
        await using var coordinator = new BatteryNotificationCoordinator(polling, notifications);
        await coordinator.StartAsync();
        await polling.ApplyConfigurationAsync(Snapshot(Device("one")));
        await Eventually(() => notifications.Calls.Count == 1);

        await polling.RefreshAllAsync();
        await polling.RefreshAllAsync();
        await Task.Delay(50);

        var call = Assert.Single(notifications.Calls);
        Assert.Equal("one", call.DeviceId);
        Assert.Equal("fully-charged", call.Kind);
    }

    [Fact]
    public async Task RecoveryAndManualRefreshDoNotResetLowBatterySuppression()
    {
        var notifications = new RecordingNotificationService();
        await using var polling = CreatePolling(new SequenceFactory(
            new BatteryReading(29, BatteryChargeState.NotCharging),
            new BatteryReading(70, BatteryChargeState.NotCharging),
            new BatteryReading(5, BatteryChargeState.NotCharging)));
        await using var coordinator = new BatteryNotificationCoordinator(polling, notifications);
        await coordinator.StartAsync();
        await polling.ApplyConfigurationAsync(Snapshot(Device("one")));
        await Eventually(() => notifications.Calls.Count == 1);

        await polling.RefreshAllAsync();
        await polling.RefreshAllAsync();
        await Task.Delay(50);
        Assert.Single(notifications.Calls);
    }

    [Fact]
    public async Task RepeatedOneHundredPercentAndRecoveryDoNotResetFullSuppression()
    {
        var notifications = new RecordingNotificationService();
        await using var polling = CreatePolling(new SequenceFactory(
            new BatteryReading(100, BatteryChargeState.NotCharging),
            new BatteryReading(99, BatteryChargeState.Charging),
            new BatteryReading(100, BatteryChargeState.Charging),
            BatteryReading.Offline,
            new BatteryReading(100, BatteryChargeState.NotCharging)));
        await using var coordinator = new BatteryNotificationCoordinator(polling, notifications);
        await coordinator.StartAsync();
        await polling.ApplyConfigurationAsync(Snapshot(Device("one")));
        await Eventually(() => notifications.Calls.Count == 1);

        for (var attempt = 0; attempt < 4; attempt++) await polling.RefreshAllAsync();
        await Task.Delay(50);

        var call = Assert.Single(notifications.Calls);
        Assert.Equal("fully-charged", call.Kind);
    }

    [Fact]
    public async Task DifferentDeviceIdsNotifyIndependently()
    {
        var notifications = new RecordingNotificationService();
        await using var polling = CreatePolling(new SequenceFactory(
            new BatteryReading(100, BatteryChargeState.NotCharging),
            new BatteryReading(100, BatteryChargeState.Charging)));
        await using var coordinator = new BatteryNotificationCoordinator(polling, notifications);
        await coordinator.StartAsync();
        await polling.ApplyConfigurationAsync(Snapshot(Device("one", "Same name"), Device("two", "Same name")));
        await Eventually(() => notifications.Calls.Count == 2);

        Assert.Equal(new[] { "one", "two" }, notifications.Calls.Select(call => call.DeviceId).OrderBy(id => id));
        Assert.All(notifications.Calls, call => Assert.Equal("fully-charged", call.Kind));
    }

    [Fact]
    public async Task SameDeviceCanReceiveOneOfEachNotificationKind()
    {
        var notifications = new RecordingNotificationService();
        await using var polling = CreatePolling(new SequenceFactory(
            new BatteryReading(5, BatteryChargeState.NotCharging),
            new BatteryReading(100, BatteryChargeState.Charging)));
        await using var coordinator = new BatteryNotificationCoordinator(polling, notifications);
        await coordinator.StartAsync();
        await polling.ApplyConfigurationAsync(Snapshot(Device("one")));
        await Eventually(() => notifications.Calls.Count == 1);

        await polling.RefreshDeviceAsync("one");
        await Eventually(() => notifications.Calls.Count == 2);

        Assert.Equal(new[] { "low-battery", "fully-charged" }, notifications.Calls.Select(call => call.Kind).ToArray());
    }

    [Fact]
    public async Task CaseVariantIdAfterReconfigurationDoesNotNotifyAgain()
    {
        var notifications = new RecordingNotificationService();
        await using var polling = CreatePolling(new SequenceFactory(
            new BatteryReading(5, BatteryChargeState.NotCharging), new BatteryReading(5, BatteryChargeState.NotCharging)));
        await using var coordinator = new BatteryNotificationCoordinator(polling, notifications);
        await coordinator.StartAsync();
        await polling.ApplyConfigurationAsync(Snapshot(Device("device")));
        await Eventually(() => notifications.Calls.Count == 1);

        await polling.ApplyConfigurationAsync(Snapshot(Device("DEVICE")));
        await Task.Delay(100);

        Assert.Single(notifications.Calls);
    }

    [Fact]
    public async Task DeliveryFailureIsNotRetriedAndDoesNotChangeTheBatteryState()
    {
        var notifications = new RecordingNotificationService { ThrowOnShow = true };
        await using var polling = CreatePolling(new SequenceFactory(
            new BatteryReading(100, BatteryChargeState.NotCharging), new BatteryReading(100, BatteryChargeState.NotCharging)));
        await using var coordinator = new BatteryNotificationCoordinator(polling, notifications);
        await coordinator.StartAsync();
        await polling.ApplyConfigurationAsync(Snapshot(Device("one")));
        await Eventually(() => notifications.Calls.Count == 1);

        await polling.RefreshAllAsync();
        await Task.Delay(50);

        var call = Assert.Single(notifications.Calls);
        Assert.Equal("fully-charged", call.Kind);
        Assert.Equal(100, polling.States.Single().Percentage);
        Assert.Null(polling.States.Single().Error);
    }

    [Fact]
    public async Task ANewCoordinatorStartsANewPerRunNotificationWindow()
    {
        var firstNotifications = new RecordingNotificationService();
        await using var firstPolling = CreatePolling(new SequenceFactory(new BatteryReading(100, BatteryChargeState.NotCharging)));
        await using (var firstCoordinator = new BatteryNotificationCoordinator(firstPolling, firstNotifications))
        {
            await firstCoordinator.StartAsync();
            await firstPolling.ApplyConfigurationAsync(Snapshot(Device("one")));
            await Eventually(() => firstNotifications.Calls.Count == 1);
        }

        var secondNotifications = new RecordingNotificationService();
        await using var secondPolling = CreatePolling(new SequenceFactory(new BatteryReading(100, BatteryChargeState.NotCharging)));
        await using var secondCoordinator = new BatteryNotificationCoordinator(secondPolling, secondNotifications);
        await secondCoordinator.StartAsync();
        await secondPolling.ApplyConfigurationAsync(Snapshot(Device("one")));
        await Eventually(() => secondNotifications.Calls.Count == 1);
        Assert.Equal("fully-charged", Assert.Single(secondNotifications.Calls).Kind);
    }

    private static PollingCoordinator CreatePolling(IBatteryAdapterFactory factory) => new(new AdapterRegistry(new[] { factory }));

    private static ConfigurationSnapshot Snapshot(params DeviceConfiguration[] devices) =>
        new("devices.json", "", devices);

    private static DeviceConfiguration Device(string id, string? name = null) => new()
    {
        Id = id,
        Name = name ?? id,
        AdapterId = "test",
        AdapterSettings = JsonDocument.Parse("{}").RootElement,
        PollIntervalSeconds = 60,
        TimeoutSeconds = 1
    };

    private static async Task Eventually(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        Assert.True(condition(), "Condition was not reached before the timeout.");
    }

    private sealed class SequenceFactory(params BatteryReading[] readings) : IBatteryAdapterFactory
    {
        private readonly ConcurrentQueue<BatteryReading> _readings = new(readings);

        public string AdapterId => "test";

        public void ValidateSettings(JsonElement settings) { }

        public IBatteryAdapter CreateAdapter(DeviceConfiguration device) => new SequenceAdapter(_readings);
    }

    private sealed class SequenceAdapter(ConcurrentQueue<BatteryReading> readings) : IBatteryAdapter
    {
        public Task<BatteryReading> GetBatteryStatus(CancellationToken cancellationToken = default) =>
            Task.FromResult(readings.TryDequeue(out var reading)
                ? reading
                : new BatteryReading(50, BatteryChargeState.NotCharging));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record NotificationCall(string DeviceId, string DeviceName, string Kind);

    private sealed class RecordingNotificationService : IBatteryNotificationService
    {
        public ConcurrentQueue<NotificationCall> Calls { get; } = new();

        public bool ThrowOnShow { get; init; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ShowLowBatteryAsync(string deviceId, string deviceName, CancellationToken cancellationToken = default) =>
            RecordAsync(deviceId, deviceName, "low-battery");

        public Task ShowFullyChargedAsync(string deviceId, string deviceName, CancellationToken cancellationToken = default) =>
            RecordAsync(deviceId, deviceName, "fully-charged");

        private Task RecordAsync(string deviceId, string deviceName, string kind)
        {
            Calls.Enqueue(new NotificationCall(deviceId, deviceName, kind));
            return ThrowOnShow ? Task.FromException(new InvalidOperationException("No notification service.")) : Task.CompletedTask;
        }
    }
}

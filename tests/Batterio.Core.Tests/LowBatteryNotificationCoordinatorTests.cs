using System.Text.Json;
using System.Collections.Concurrent;
using Batterio.Core;
using Batterio.Core.Abstractions;
using Batterio.Core.Notifications;

namespace Batterio.Core.Tests;

public sealed class LowBatteryNotificationCoordinatorTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(29, true)]
    [InlineData(30, false)]
    [InlineData(31, false)]
    public async Task SuccessfulReadingsBelowThirtyNotifyOnce(int percentage, bool shouldNotify)
    {
        var notifications = new RecordingNotificationService();
        await using var polling = CreatePolling(new SequenceFactory(percentage));
        await using var coordinator = new LowBatteryNotificationCoordinator(polling, notifications);
        await coordinator.StartAsync();
        await polling.ApplyConfigurationAsync(Snapshot(Device("one")));

        await Eventually(() => polling.States.Single().Percentage == percentage);
        await Eventually(() => notifications.Calls.Count == (shouldNotify ? 1 : 0));
        Assert.Equal(shouldNotify ? 1 : 0, notifications.Calls.Count);
    }

    [Fact]
    public async Task RecoveryAndManualRefreshDoNotResetPerRunSuppression()
    {
        var notifications = new RecordingNotificationService();
        await using var polling = CreatePolling(new SequenceFactory(29, 70, 5));
        await using var coordinator = new LowBatteryNotificationCoordinator(polling, notifications);
        await coordinator.StartAsync();
        await polling.ApplyConfigurationAsync(Snapshot(Device("one")));
        await Eventually(() => notifications.Calls.Count == 1);

        await polling.RefreshAllAsync();
        await Eventually(() => polling.States.Single().Percentage == 70);
        await polling.RefreshAllAsync();
        await Eventually(() => polling.States.Single().Percentage == 5);

        await Task.Delay(50);
        Assert.Single(notifications.Calls);
    }

    [Fact]
    public async Task SeparateIdsNotifyEvenWhenTheirDisplayNamesMatch()
    {
        var notifications = new RecordingNotificationService();
        await using var polling = CreatePolling(new SequenceFactory(5, 5));
        await using var coordinator = new LowBatteryNotificationCoordinator(polling, notifications);
        await coordinator.StartAsync();
        await polling.ApplyConfigurationAsync(Snapshot(Device("one", "Same name"), Device("two", "Same name")));

        await Eventually(() => notifications.Calls.Count == 2);
        Assert.Equal(new[] { "one", "two" }, notifications.Calls.Select(call => call.DeviceId).OrderBy(id => id));
    }

    [Fact]
    public async Task CaseVariantIdAfterReconfigurationDoesNotNotifyAgain()
    {
        var notifications = new RecordingNotificationService();
        await using var polling = CreatePolling(new SequenceFactory(5, 5));
        await using var coordinator = new LowBatteryNotificationCoordinator(polling, notifications);
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
        await using var polling = CreatePolling(new SequenceFactory(5, 5));
        await using var coordinator = new LowBatteryNotificationCoordinator(polling, notifications);
        await coordinator.StartAsync();
        await polling.ApplyConfigurationAsync(Snapshot(Device("one")));
        await Eventually(() => notifications.Calls.Count == 1);

        await polling.RefreshAllAsync();
        await Eventually(() => polling.States.Single().Percentage == 5);
        await Task.Delay(50);

        Assert.Single(notifications.Calls);
        Assert.Null(polling.States.Single().Error);
    }

    [Fact]
    public async Task ANewCoordinatorStartsANewPerRunNotificationWindow()
    {
        var firstNotifications = new RecordingNotificationService();
        await using var firstPolling = CreatePolling(new SequenceFactory(5));
        await using (var firstCoordinator = new LowBatteryNotificationCoordinator(firstPolling, firstNotifications))
        {
            await firstCoordinator.StartAsync();
            await firstPolling.ApplyConfigurationAsync(Snapshot(Device("one")));
            await Eventually(() => firstNotifications.Calls.Count == 1);
        }

        var secondNotifications = new RecordingNotificationService();
        await using var secondPolling = CreatePolling(new SequenceFactory(5));
        await using var secondCoordinator = new LowBatteryNotificationCoordinator(secondPolling, secondNotifications);
        await secondCoordinator.StartAsync();
        await secondPolling.ApplyConfigurationAsync(Snapshot(Device("one")));
        await Eventually(() => secondNotifications.Calls.Count == 1);
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
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(condition(), "Condition was not reached before the timeout.");
    }

    private sealed class SequenceFactory(params int[] percentages) : IBatteryAdapterFactory
    {
        private readonly ConcurrentQueue<int> _percentages = new(percentages);

        public string AdapterId => "test";

        public void ValidateSettings(JsonElement settings) { }

        public IBatteryAdapter CreateAdapter(DeviceConfiguration device) => new SequenceAdapter(_percentages);
    }

    private sealed class SequenceAdapter(ConcurrentQueue<int> percentages) : IBatteryAdapter
    {
        public Task<BatteryReading> GetBatteryStatus(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BatteryReading(percentages.TryDequeue(out var value) ? value : 50, BatteryChargeState.NotCharging));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingNotificationService : IBatteryNotificationService
    {
        public ConcurrentQueue<(string DeviceId, string DeviceName)> Calls { get; } = new();

        public bool ThrowOnShow { get; init; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ShowLowBatteryAsync(string deviceId, string deviceName, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue((deviceId, deviceName));
            return ThrowOnShow ? Task.FromException(new InvalidOperationException("No notification service.")) : Task.CompletedTask;
        }
    }
}

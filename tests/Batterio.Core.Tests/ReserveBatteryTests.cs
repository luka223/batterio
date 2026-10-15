using System.Collections.Concurrent;
using System.Text.Json;
using Batterio.Core;
using Batterio.Core.Abstractions;
using Batterio.Core.Notifications;

namespace Batterio.Core.Tests;

public sealed class ReserveBatteryTests
{
    private static BatteryReading Reserve(int percentage, BatteryChargeState state = BatteryChargeState.NotCharging) =>
        new(percentage, state);

    // 6.1 BatteryReading

    [Fact]
    public void AReserveCanBeAttachedToAReading()
    {
        var reading = new BatteryReading(80, BatteryChargeState.NotCharging) { Reserve = Reserve(45, BatteryChargeState.Charging) };

        Assert.Equal(80, reading.Percentage);
        Assert.Equal(Reserve(45, BatteryChargeState.Charging), reading.Reserve);
        Assert.Null(new BatteryReading(80, BatteryChargeState.NotCharging).Reserve);
        Assert.Null(BatteryReading.Offline.Reserve);
    }

    [Fact]
    public void AnOfflineReadingCanStillCarryAReserve()
    {
        var reading = BatteryReading.Offline with { Reserve = Reserve(60) };

        Assert.Equal(BatteryChargeState.Offline, reading.ChargeState);
        Assert.Equal(60, reading.Reserve?.Percentage);
        Assert.Null(BatteryReading.Offline.Reserve);
    }

    [Fact]
    public void ANestedReserveIsRejected()
    {
        var nested = Reserve(10) with { Reserve = Reserve(20) };

        Assert.Throws<ArgumentException>(() => new BatteryReading(80, BatteryChargeState.NotCharging) { Reserve = nested });
    }

    [Fact]
    public void ReadingsDifferWhenOnlyTheReserveDiffers()
    {
        var first = new BatteryReading(80, BatteryChargeState.NotCharging) { Reserve = Reserve(45) };
        var same = new BatteryReading(80, BatteryChargeState.NotCharging) { Reserve = Reserve(45) };
        var other = new BatteryReading(80, BatteryChargeState.NotCharging) { Reserve = Reserve(46) };

        Assert.Equal(first, same);
        Assert.NotEqual(first, other);
        Assert.NotEqual(first, new BatteryReading(80, BatteryChargeState.NotCharging));
    }

    // 6.5 Catalog

    [Fact]
    public void TheCatalogContainsTheOmniWithAReserveBatteryAndItsQuery()
    {
        var omni = BuiltInDevices.Catalog.Single(device => device.Id == "steelseries-arctis-nova-pro-omni");

        Assert.Equal("SteelSeries Arctis Nova Pro Omni", omni.Name);
        Assert.True(omni.HasReserveBattery);
        Assert.Same(SteelSeriesDefinitions.NovaProOmniQuery, Assert.Single(omni.PresenceQueries));
        Assert.True(omni.CreateConfiguration().HasReserveBattery);
        Assert.All(BuiltInDevices.Catalog.Where(device => device.Id != omni.Id), device => Assert.False(device.HasReserveBattery));

        var query = SteelSeriesDefinitions.NovaProOmniQuery;
        Assert.Equal((ushort)0x1038, query.VendorId);
        Assert.Equal((ushort)0x2290, query.ProductId);
        Assert.Equal((ushort)3, query.InterfaceNumber);
        Assert.Equal((ushort)0xFFC0, query.UsagePage);
        Assert.Equal((ushort)0x0001, query.Usage);
        Assert.Equal((byte)0x01, query.RequiredReportId);
        Assert.Equal(64, query.MinimumReportLength);
    }

    [Fact]
    public void EveryCatalogDeviceHasARegisteredFactory()
    {
        var registry = new AdapterRegistry(
        [
            new KeychronQ1HeAdapterFactory(),
            new LogitechGProX2SuperstrikeAdapterFactory(),
            new LogitechGProX2LightspeedAdapterFactory(),
            new SteelSeriesArctisNovaProOmniAdapterFactory(),
            new LogitechMxMaster3sAdapterFactory(),
            new LogitechMxKeysMechanicalAdapterFactory()
        ]);

        Assert.All(BuiltInDevices.Catalog, device => Assert.True(registry.TryGetFactory(device.AdapterId, out _), device.AdapterId));
    }

    [Fact]
    public void TheReserveFlagIsReadFromJsonAndDefaultsToFalse()
    {
        Assert.True(JsonSerializer.Deserialize<DeviceConfiguration>("""{"id":"x","hasReserveBattery":true}""")!.HasReserveBattery);
        Assert.False(JsonSerializer.Deserialize<DeviceConfiguration>("""{"id":"x"}""")!.HasReserveBattery);
    }

    // 6.6 Presentation

    [Fact]
    public void AMissingReserveIsAnEmptyIconWithoutAPercentage()
    {
        var presentation = BatteryPresentationRules.CreateReserve(null);

        Assert.Equal(0, presentation.FilledSegments);
        Assert.False(presentation.IsPercentageVisible);
        Assert.Equal(string.Empty, presentation.PercentageText);
        Assert.False(presentation.IsCharging);
    }

    [Theory]
    [InlineData(0, BatteryChargeState.Charging, 0, true)]
    [InlineData(45, BatteryChargeState.Charging, 4, true)]
    [InlineData(100, BatteryChargeState.NotCharging, 10, false)]
    public void TheReserveReusesSegmentsPercentageAndChargingButKeepsAFixedColor(
        int percentage, BatteryChargeState state, int segments, bool charging)
    {
        var presentation = BatteryPresentationRules.CreateReserve(Reserve(percentage, state));

        Assert.Equal($"{percentage}%", presentation.PercentageText);
        Assert.True(presentation.IsPercentageVisible);
        Assert.Equal(segments, presentation.FilledSegments);
        Assert.Equal(charging, presentation.IsCharging);
        Assert.Equal(BatteryPresentationRules.ReserveDarkColorHex, presentation.BatteryColorHex);
    }

    [Fact]
    public void TheReserveColorsMatchTheThemes()
    {
        Assert.Equal("#FFFFFF", BatteryPresentationRules.ReserveDarkColorHex);
        Assert.Equal("#18181B", BatteryPresentationRules.ReserveLightColorHex);
    }

    [Fact]
    public void AccessibilityTextAppendsTheReserve()
    {
        var reading = new BatteryReading(80, BatteryChargeState.NotCharging) { Reserve = Reserve(45, BatteryChargeState.Charging) };

        Assert.Equal("Omni: Not charging, 80 percent. Reserve battery: Charging, 45 percent",
            BatteryPresentationRules.GetAccessibilityDescription("Omni", reading, hasReserveBattery: true));
        Assert.Equal("Omni: Not charging, 80 percent",
            BatteryPresentationRules.GetAccessibilityDescription("Omni", reading, hasReserveBattery: false));
        Assert.Equal("Omni: Offline",
            BatteryPresentationRules.GetAccessibilityDescription("Omni", BatteryReading.Offline, hasReserveBattery: true));
    }

    // 6.7 Polling

    [Fact]
    public async Task AReadingWithAReserveReachesTheSnapshotAndALaterFailureDropsIt()
    {
        var withReserve = new BatteryReading(80, BatteryChargeState.NotCharging) { Reserve = Reserve(45, BatteryChargeState.Charging) };
        await using var polling = CreatePolling(new SequenceFactory(withReserve, new InvalidOperationException("hub unplugged")));
        await polling.ApplyConfigurationAsync(Snapshot(Device("omni")));

        await Eventually(() => polling.CurrentSnapshot.States.SingleOrDefault()?.Reading.Reserve is not null);
        Assert.Equal(Reserve(45, BatteryChargeState.Charging), polling.CurrentSnapshot.States.Single().Reading.Reserve);

        await polling.RefreshAllAsync();
        await Eventually(() => polling.CurrentSnapshot.States.Single().State == BatteryChargeState.Offline);
        var state = polling.CurrentSnapshot.States.Single();
        Assert.Null(state.Reading.Reserve);
        Assert.Null(state.Percentage);
    }

    [Fact]
    public async Task AReserveOnlyChangeIsPublishedAsAStateChange()
    {
        var first = new BatteryReading(80, BatteryChargeState.NotCharging) { Reserve = Reserve(45, BatteryChargeState.Charging) };
        var second = new BatteryReading(80, BatteryChargeState.NotCharging) { Reserve = Reserve(46, BatteryChargeState.Charging) };
        await using var polling = CreatePolling(new SequenceFactory(first, second));
        await polling.ApplyConfigurationAsync(Snapshot(Device("omni")));
        await Eventually(() => polling.CurrentSnapshot.States.SingleOrDefault()?.Reading.Reserve?.Percentage == 45);
        var revision = polling.CurrentSnapshot.Revision;

        await polling.RefreshAllAsync();

        await Eventually(() => polling.CurrentSnapshot.States.Single().Reading.Reserve?.Percentage == 46);
        Assert.True(polling.CurrentSnapshot.Revision > revision);
    }

    // 6.8 Notifications cover the headset only

    [Fact]
    public async Task AFullReserveDoesNotNotifyWhileTheHeadsetIsOffline()
    {
        var notifications = new RecordingNotificationService();
        var reading = BatteryReading.Offline with { Reserve = Reserve(100) };
        await using var polling = CreatePolling(new SequenceFactory(reading, reading));
        await using var coordinator = new BatteryNotificationCoordinator(polling, notifications);
        await coordinator.StartAsync();
        await polling.ApplyConfigurationAsync(Snapshot(Device("omni")));
        await Eventually(() => polling.CurrentSnapshot.States.SingleOrDefault()?.Reading.Reserve is not null);

        await polling.RefreshAllAsync();
        await Task.Delay(100);

        Assert.Empty(notifications.Calls);
    }

    [Fact]
    public async Task ALowReserveDoesNotRaiseALowBatteryNotification()
    {
        var notifications = new RecordingNotificationService();
        var reading = new BatteryReading(80, BatteryChargeState.NotCharging) { Reserve = Reserve(5, BatteryChargeState.Charging) };
        await using var polling = CreatePolling(new SequenceFactory(reading, reading));
        await using var coordinator = new BatteryNotificationCoordinator(polling, notifications);
        await coordinator.StartAsync();
        await polling.ApplyConfigurationAsync(Snapshot(Device("omni")));
        await Eventually(() => polling.CurrentSnapshot.States.SingleOrDefault()?.Reading.Reserve is not null);

        await polling.RefreshAllAsync();
        await Task.Delay(100);

        Assert.Empty(notifications.Calls);
    }

    [Fact]
    public async Task ALowHeadsetStillNotifiesWhateverTheReserveHolds()
    {
        var notifications = new RecordingNotificationService();
        var reading = new BatteryReading(12, BatteryChargeState.NotCharging) { Reserve = Reserve(100) };
        await using var polling = CreatePolling(new SequenceFactory(reading));
        await using var coordinator = new BatteryNotificationCoordinator(polling, notifications);
        await coordinator.StartAsync();
        await polling.ApplyConfigurationAsync(Snapshot(Device("omni")));

        await Eventually(() => notifications.Calls.Count == 1);
        Assert.Equal("low-battery", Assert.Single(notifications.Calls).Kind);
    }

    private static PollingCoordinator CreatePolling(IBatteryAdapterFactory factory) => new(new AdapterRegistry([factory]));

    private static ConfigurationSnapshot Snapshot(params DeviceConfiguration[] devices) => new("devices.json", "", devices);

    private static DeviceConfiguration Device(string id) => new()
    {
        Id = id,
        Name = id,
        AdapterId = "test",
        AdapterSettings = JsonDocument.Parse("{}").RootElement,
        HasReserveBattery = true,
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

    private sealed class SequenceFactory(params object[] values) : IBatteryAdapterFactory
    {
        private readonly ConcurrentQueue<object> _values = new(values);

        public string AdapterId => "test";

        public void ValidateSettings(JsonElement settings) { }

        public IBatteryAdapter CreateAdapter(DeviceConfiguration device) => new SequenceAdapter(_values);
    }

    private sealed class SequenceAdapter(ConcurrentQueue<object> values) : IBatteryAdapter
    {
        public Task<BatteryReading> GetBatteryStatus(CancellationToken cancellationToken = default) =>
            values.TryDequeue(out var next)
                ? next switch
                {
                    BatteryReading reading => Task.FromResult(reading),
                    Exception exception => Task.FromException<BatteryReading>(exception),
                    _ => throw new InvalidOperationException()
                }
                : Task.FromResult(BatteryReading.Offline);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record NotificationCall(string DeviceId, string Kind);

    private sealed class RecordingNotificationService : IBatteryNotificationService
    {
        public ConcurrentQueue<NotificationCall> Calls { get; } = new();

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ShowLowBatteryAsync(string deviceId, string deviceName, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue(new NotificationCall(deviceId, "low-battery"));
            return Task.CompletedTask;
        }

        public Task ShowFullyChargedAsync(string deviceId, string deviceName, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue(new NotificationCall(deviceId, "fully-charged"));
            return Task.CompletedTask;
        }
    }
}

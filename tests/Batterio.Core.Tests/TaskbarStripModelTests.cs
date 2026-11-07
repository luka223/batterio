using Batterio.Core;

namespace Batterio.Core.Tests;

public sealed class TaskbarStripModelTests
{
    [Fact]
    public void SelectedOptedInOnlineDevicesAreIncludedWithTheirKindAndChargeState()
    {
        var selection = Selection(
            Device("mouse", DeviceKind.Mouse, selected: true, onTaskbar: true),
            Device("keys", DeviceKind.Keyboard, selected: true, onTaskbar: true));
        var polling = Polling(
            State("mouse", new BatteryReading(64, BatteryChargeState.NotCharging)),
            State("keys", new BatteryReading(12, BatteryChargeState.Charging)));

        var items = TaskbarStripModel.Build(selection, polling);

        Assert.Equal(2, items.Count);
        Assert.Equal(new TaskbarStripItem("mouse", DeviceKind.Mouse, 64, false), items[0]);
        Assert.Equal(new TaskbarStripItem("keys", DeviceKind.Keyboard, 12, true), items[1]);
    }

    [Fact]
    public void DevicesNotOptedInAreHidden()
    {
        var selection = Selection(Device("mouse", DeviceKind.Mouse, selected: true, onTaskbar: false));
        var polling = Polling(State("mouse", new BatteryReading(64, BatteryChargeState.NotCharging)));

        Assert.Empty(TaskbarStripModel.Build(selection, polling));
    }

    [Fact]
    public void DevicesNotSelectedForThePopupAreHiddenEvenWhenOptedIn()
    {
        var selection = Selection(Device("mouse", DeviceKind.Mouse, selected: false, onTaskbar: true));
        var polling = Polling(State("mouse", new BatteryReading(64, BatteryChargeState.NotCharging)));

        Assert.Empty(TaskbarStripModel.Build(selection, polling));
    }

    [Fact]
    public void OfflineDevicesAndDevicesWithoutAPercentageAreIncludedAsOfflineItems()
    {
        var selection = Selection(
            Device("offline", DeviceKind.Mouse, selected: true, onTaskbar: true),
            Device("unread", DeviceKind.Keyboard, selected: true, onTaskbar: true),
            Device("charging-unknown", DeviceKind.Headset, selected: true, onTaskbar: true));
        var polling = Polling(
            State("offline", BatteryReading.Offline),
            State("unread", new BatteryReading(null, BatteryChargeState.NotCharging)),
            State("charging-unknown", new BatteryReading(null, BatteryChargeState.Charging)));

        var items = TaskbarStripModel.Build(selection, polling);

        Assert.Equal(3, items.Count);
        Assert.All(items, item =>
        {
            Assert.True(item.IsOffline);
            Assert.Null(item.Percentage);
            Assert.False(item.IsCharging);
        });
        Assert.Equal(
            new[] { DeviceKind.Mouse, DeviceKind.Keyboard, DeviceKind.Headset },
            items.Select(item => item.Kind));
    }

    [Fact]
    public void OptedInDevicesWithoutAPollingStateAreIncludedAsOffline()
    {
        // A disconnected device is not polled, but it is still ticked in Settings.
        var selection = Selection(Device("mouse", DeviceKind.Mouse, selected: true, onTaskbar: true, present: false));

        var item = Assert.Single(TaskbarStripModel.Build(selection, Polling()));

        Assert.Equal("mouse", item.DeviceId);
        Assert.True(item.IsOffline);
    }

    [Fact]
    public void OnlineDevicesAreNotMarkedOffline()
    {
        var selection = Selection(Device("mouse", DeviceKind.Mouse, selected: true, onTaskbar: true));
        var polling = Polling(State("mouse", new BatteryReading(0, BatteryChargeState.NotCharging)));

        var item = Assert.Single(TaskbarStripModel.Build(selection, polling));

        Assert.False(item.IsOffline);
        Assert.Equal(0, item.Percentage);
    }

    [Fact]
    public void ReserveBatteryIsIgnored()
    {
        var selection = Selection(Device("omni", DeviceKind.Headset, selected: true, onTaskbar: true));
        var polling = Polling(State("omni", new BatteryReading(90, BatteryChargeState.NotCharging)
        {
            Reserve = new BatteryReading(33, BatteryChargeState.Charging)
        }));

        var item = Assert.Single(TaskbarStripModel.Build(selection, polling));

        Assert.Equal(90, item.Percentage);
        Assert.False(item.IsCharging);
    }

    [Fact]
    public void ReserveOnlyChargingDoesNotMarkTheMainBatteryAsCharging()
    {
        var selection = Selection(Device("omni", DeviceKind.Headset, selected: true, onTaskbar: true));
        var polling = Polling(State("omni", new BatteryReading(50, BatteryChargeState.Charging)
        {
            Reserve = new BatteryReading(100, BatteryChargeState.NotCharging)
        }));

        var item = Assert.Single(TaskbarStripModel.Build(selection, polling));

        Assert.True(item.IsCharging);
        Assert.Equal(50, item.Percentage);
    }

    [Fact]
    public void ItemOrderFollowsTheDeviceListAndIdsMatchCaseInsensitively()
    {
        var selection = Selection(
            Device("a-device", DeviceKind.Mouse, selected: true, onTaskbar: true),
            Device("b-device", DeviceKind.Keyboard, selected: true, onTaskbar: true),
            Device("c-device", DeviceKind.Headset, selected: true, onTaskbar: true));
        var polling = Polling(
            State("C-DEVICE", new BatteryReading(30, BatteryChargeState.NotCharging)),
            State("a-device", new BatteryReading(10, BatteryChargeState.NotCharging)),
            State("b-device", BatteryReading.Offline));

        var items = TaskbarStripModel.Build(selection, polling);

        // The order does not change when a device goes offline or comes back.
        Assert.Equal(new[] { "a-device", "b-device", "c-device" }, items.Select(item => item.DeviceId));
        Assert.Equal(30, items[2].Percentage);
    }

    [Fact]
    public void OnlyOptedInDevicesAppearAmongSeveral()
    {
        var selection = Selection(
            Device("mouse", DeviceKind.Mouse, selected: true, onTaskbar: true),
            Device("keys", DeviceKind.Keyboard, selected: true, onTaskbar: false));
        var polling = Polling(
            State("mouse", new BatteryReading(80, BatteryChargeState.NotCharging)),
            State("keys", new BatteryReading(70, BatteryChargeState.NotCharging)));

        var item = Assert.Single(TaskbarStripModel.Build(selection, polling));

        Assert.Equal("mouse", item.DeviceId);
    }

    private static DeviceSelectionSnapshot Selection(params DeviceSelectionState[] devices) =>
        new(1, isLoading: false, devices);

    private static PollingSnapshot Polling(params DeviceBatteryState[] states) => new(1, states);

    private static DeviceSelectionState Device(
        string id,
        DeviceKind kind,
        bool selected,
        bool onTaskbar,
        bool present = true) => new()
    {
        Device = new BuiltInDeviceDefinition
        {
            Id = id,
            Name = id,
            AdapterId = id,
            Kind = kind,
            PresenceQueries = []
        },
        IsPresent = present,
        IsSelected = selected,
        IsOnTaskbar = onTaskbar
    };

    private static DeviceBatteryState State(string id, BatteryReading reading) => new()
    {
        Configuration = new DeviceConfiguration { Id = id, Name = id, AdapterId = id },
        Reading = reading
    };
}

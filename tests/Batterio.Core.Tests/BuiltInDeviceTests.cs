using System.Text.Json;
using Batterio.Core;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

public sealed class BuiltInDeviceTests
{
    [Fact]
    public void SuperstrikeRoutesPreferTheCableAndTheOtherUsbDevicesHaveOneReceiverRoute()
    {
        var superstrike = BuiltInDevices.Catalog.Single(device => device.Id == "logitech-g-pro-x2-superstrike");
        var cable = Assert.IsType<UsbHidRouteDescriptor>(superstrike.Routes[0]);
        var receiver = Assert.IsType<UsbHidRouteDescriptor>(superstrike.Routes[1]);
        var lightspeed = BuiltInDevices.Catalog.Single(device => device.Id == "logitech-g-pro-x2-lightspeed");

        Assert.Equal(2, superstrike.Routes.Count);
        Assert.Equal(DeviceRouteKind.UsbCable, cable.Kind);
        Assert.Equal(DeviceRouteKind.UsbReceiver, receiver.Kind);
        var wired = cable.Query;
        Assert.Equal((ushort)0x046D, wired.VendorId);
        Assert.Equal((ushort)0xC0A8, wired.ProductId);
        Assert.Equal((ushort)2, wired.InterfaceNumber);
        Assert.Equal((ushort)0xFF00, wired.UsagePage);
        Assert.Equal((ushort)0x0002, wired.Usage);
        Assert.Equal(20, wired.MinimumReportLength);
        Assert.Equal((byte)0x11, wired.RequiredReportId);
        Assert.Equal((ushort)0xC54D, receiver.Query.ProductId);
        var headset = Assert.IsType<UsbHidRouteDescriptor>(Assert.Single(lightspeed.Routes));
        Assert.Equal(DeviceRouteKind.UsbReceiver, headset.Kind);
        Assert.Same(LogitechGProX2Lightspeed.LightspeedQuery, headset.Query);
    }

    [Fact]
    public void UsbRouteDescriptorsAcceptOnlyTheUsbKinds()
    {
        var query = KeychronQ1He.LinkRawHidQuery;
        Assert.Equal(DeviceRouteKind.UsbCable, new UsbHidRouteDescriptor(DeviceRouteKind.UsbCable, query).Kind);
        Assert.Equal(DeviceRouteKind.UsbReceiver, new UsbHidRouteDescriptor(DeviceRouteKind.UsbReceiver, query).Kind);
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsbHidRouteDescriptor(DeviceRouteKind.LogiBolt, query));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsbHidRouteDescriptor(DeviceRouteKind.HidppBluetooth, query));
    }

    [Fact]
    public void EveryCatalogDeviceHasAProfileWithTheSameRoutes()
    {
        Assert.Equal(BuiltInDevices.Catalog.Count, BuiltInDevices.Profiles.Count);
        foreach (var device in BuiltInDevices.Catalog)
        {
            var profile = Assert.Single(BuiltInDevices.Profiles, entry => entry.AdapterId == device.AdapterId);
            Assert.Same(profile.Routes, device.Routes);
            Assert.All(device.Routes, descriptor => Assert.Contains(descriptor.Kind, profile.CreateRoutes(
                new RouteContext(new EmptyHidCollectionProvider(), new LogiBoltReceiverManager(new EmptyHidCollectionProvider()),
                    new NullBatterioLog(), TimeProvider.System, new RouteTimings())).Keys));
        }
    }

    [Fact]
    public async Task BuiltInDevicesContainTheShippedAdapters()
    {
        var devices = BuiltInDevices.Create();
        Assert.Equal(6, devices.Count);
        Assert.All(devices, device => Assert.Equal(300, device.PollIntervalSeconds));
        Assert.Equal(
            new[]
            {
                "keychron-q1-he", "logitech-g-pro-x2-superstrike", "logitech-g-pro-x2-lightspeed", "steelseries-arctis-nova-pro-omni",
                "logitech-mx-master-3s", "logitech-mx-keys-mechanical"
            },
            devices.Select(device => device.AdapterId));

        await using var coordinator = new PollingCoordinator(new AdapterRegistry([
            new FixedFactory("keychron-q1-he", new BatteryReading(100, BatteryChargeState.NotCharging)),
            new FixedFactory("logitech-g-pro-x2-superstrike", new BatteryReading(64, BatteryChargeState.NotCharging)),
            new FixedFactory("logitech-g-pro-x2-lightspeed", new BatteryReading(15, BatteryChargeState.Charging)),
            new FixedFactory("steelseries-arctis-nova-pro-omni", new BatteryReading(90, BatteryChargeState.NotCharging)),
            new FixedFactory("logitech-mx-master-3s", new BatteryReading(72, BatteryChargeState.NotCharging)),
            new FixedFactory("logitech-mx-keys-mechanical", new BatteryReading(41, BatteryChargeState.Charging))]));
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readings = 0;
        coordinator.StateChanged += (_, e) =>
        {
            if (e.State.Percentage is not null && Interlocked.Increment(ref readings) == 6)
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
        Assert.Equal(13, readings);
    }

    [Fact]
    public void EveryCatalogEntryHasTheExpectedDeviceKind()
    {
        var expected = new Dictionary<string, DeviceKind>
        {
            ["keychron-q1-he"] = DeviceKind.Keyboard,
            ["logitech-mx-keys-mechanical"] = DeviceKind.Keyboard,
            ["logitech-g-pro-x2-superstrike"] = DeviceKind.Mouse,
            ["logitech-g-pro-x2-lightspeed"] = DeviceKind.Mouse,
            ["logitech-mx-master-3s"] = DeviceKind.Mouse,
            ["steelseries-arctis-nova-pro-omni"] = DeviceKind.Headset
        };

        Assert.Equal(expected.Count, BuiltInDevices.Catalog.Count);
        foreach (var device in BuiltInDevices.Catalog)
        {
            Assert.Equal(expected[device.Id], device.Kind);
            Assert.Equal(device.Kind, device.CreateConfiguration().Kind);
        }
    }

    [Fact]
    public void KeychronQ1HeIsFirstAndUsesTheLinkRawHidQuery()
    {
        var device = BuiltInDevices.Catalog[0];
        Assert.Equal("keychron-q1-he", device.Id);
        var route = Assert.IsType<UsbHidRouteDescriptor>(Assert.Single(device.Routes));
        Assert.Equal(DeviceRouteKind.UsbReceiver, route.Kind);
        Assert.Same(KeychronQ1He.LinkRawHidQuery, route.Query);

        var query = KeychronQ1He.LinkRawHidQuery;
        Assert.Equal((ushort)0x3434, query.VendorId);
        Assert.Equal((ushort)0xD030, query.ProductId);
        Assert.Equal((ushort)3, query.InterfaceNumber);
        Assert.Equal((ushort)0xFF60, query.UsagePage);
        Assert.Equal((ushort)0x0061, query.Usage);
        Assert.Equal(33, query.MinimumReportLength);
        Assert.Null(query.RequiredReportId);
    }

    [Fact]
    public void MxDevicesDeclareTheirOwnBluetoothAndBoltRoutes()
    {
        foreach (var (id, productId) in new[] { ("logitech-mx-master-3s", (ushort)0xB034), ("logitech-mx-keys-mechanical", (ushort)0xB366) })
        {
            var device = BuiltInDevices.Catalog.Single(entry => entry.Id == id);
            Assert.Equal(2, device.Routes.Count);
            Assert.Equal(new HidppBluetoothRouteDescriptor(productId), device.Routes[0]);
            var bolt = Assert.IsType<LogiBoltRouteDescriptor>(device.Routes[1]);
            Assert.Equal(productId, bolt.WirelessProductId);
            Assert.NotEmpty(bolt.Names!);
        }

        Assert.Equal("logitech-mx-master-3s", LogitechMxMaster3s.Identity.AdapterId);
        Assert.Equal("logitech-mx-keys-mechanical", LogitechMxKeysMechanical.Identity.AdapterId);

        var query = LogiBoltReceiverManager.BoltReceiverQuery;
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
        var empty = new EmptyHidCollectionProvider();
        await using var keychron = RouteHarness.Create(KeychronQ1He.Profile, empty);
        await using var superstrike = RouteHarness.Create(LogitechGProX2Superstrike.Profile, empty);
        await using var lightspeed = RouteHarness.Create(LogitechGProX2Lightspeed.Profile, empty);
        await using var omni = RouteHarness.Create(SteelSeriesArctisNovaProOmni.Profile, empty);
        await using var mxMaster = RouteHarness.Create(LogitechMxMaster3s.Profile, empty);
        await using var mxKeys = RouteHarness.Create(LogitechMxKeysMechanical.Profile, empty);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => keychron.GetBatteryStatus(cancellation.Token));
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

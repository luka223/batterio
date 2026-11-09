using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

public sealed class HidppBluetoothRouteTests
{
    private const string MouseEndpoint = "bt-b034";
    private const string KeyboardEndpoint = "bt-b366";

    private static readonly TimeSpan FastPing = TimeSpan.FromMilliseconds(150);

    [Fact]
    public async Task DeviceIsReadDirectlyOnDeviceIndex0xFFWithoutAnyIdentityRequest()
    {
        var hid = new FakeHidpp().Bluetooth(MouseEndpoint, 0xB034, FakeHidppDevice.MxMaster3s(percentage: 20));
        var route = new HidppBluetoothRoute("MX Master 3S", hid, pingTimeout: FastPing);

        var reading = await route.ReadAsync(Route(hid, MouseEndpoint, 0xB034), CancellationToken.None);

        Assert.Equal(20, reading.Percentage);
        Assert.Equal(BatteryChargeState.NotCharging, reading.ChargeState);
        Assert.Equal(new[] { MouseEndpoint }, hid.OpenedPaths);
        Assert.All(hid.Writes, write => Assert.Equal(HidppBluetoothRoute.DirectDeviceIndex, write.Report[1]));
        // The product ID in the path is the identity: neither Device Information (0x0003) nor Device Name (0x0005) is requested.
        Assert.DoesNotContain(hid.Writes, write => write.Report[2] == 0 && write.Report[3] == 0x01 &&
                                                   write.Report[4] == 0x00 && write.Report[5] is 0x03 or 0x05);
        // A ping, the feature lookup and two battery calls: no scan, no probing of other indices.
        Assert.True(hid.Writes.Count() <= 5, $"{hid.Writes.Count()} requests were sent.");
    }

    [Fact]
    public async Task MxMechanicalOverBluetoothReportsCharging()
    {
        var hid = new FakeHidpp().Bluetooth(KeyboardEndpoint, 0xB366,
            FakeHidppDevice.MxMechanical(percentage: 65) with { UnifiedStatus = 0x01, ExternalPower = 1 });
        var route = new HidppBluetoothRoute("MX Keys Mechanical", hid, pingTimeout: FastPing);

        var reading = await route.ReadAsync(Route(hid, KeyboardEndpoint, 0xB366), CancellationToken.None);

        Assert.Equal(65, reading.Percentage);
        Assert.Equal(BatteryChargeState.Charging, reading.ChargeState);
    }

    [Fact]
    public async Task BatteryStatusIsUsedWhenUnifiedBatteryIsAbsent()
    {
        var hid = new FakeHidpp().Bluetooth(KeyboardEndpoint, 0xB366, FakeHidppDevice.MxMechanical(percentage: 80, legacyStatus: 0x01, unified: false));
        var route = new HidppBluetoothRoute("MX Keys Mechanical", hid, pingTimeout: FastPing);

        var reading = await route.ReadAsync(Route(hid, KeyboardEndpoint, 0xB366), CancellationToken.None);

        Assert.Equal(80, reading.Percentage);
        Assert.Equal(BatteryChargeState.Charging, reading.ChargeState);
    }

    [Fact]
    public async Task BatteryStatusErrorCodesAreReportedAsBatteryErrors()
    {
        var hid = new FakeHidpp().Bluetooth(KeyboardEndpoint, 0xB366, FakeHidppDevice.MxMechanical(percentage: 50, legacyStatus: 0x06, unified: false));
        var route = new HidppBluetoothRoute("MX Keys Mechanical", hid, pingTimeout: FastPing);

        var exception = await Assert.ThrowsAsync<BatteryAdapterException>(() => route.ReadAsync(Route(hid, KeyboardEndpoint, 0xB366), CancellationToken.None));
        Assert.Equal(BatteryAdapterErrorKind.BatteryReportedError, exception.Kind);
    }

    [Fact]
    public async Task ASleepingDeviceIsReportedAsOfflineNotAsAFailure()
    {
        var hid = new FakeHidpp().Bluetooth(MouseEndpoint, 0xB034, device: null);
        var route = new HidppBluetoothRoute("MX Master 3S", hid, pingTimeout: FastPing);
        var started = System.Diagnostics.Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<BatteryAdapterException>(() => route.ReadAsync(Route(hid, MouseEndpoint, 0xB034), CancellationToken.None));

        Assert.Equal(BatteryAdapterErrorKind.DeviceOffline, exception.Kind);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), $"Took {started.Elapsed}.");
    }

    [Fact]
    public async Task ADeviceThatIsNotConnectedRejectsTheWriteAndIsReportedAsOffline()
    {
        var hid = new FakeHidpp().Bluetooth(MouseEndpoint, 0xB034, FakeHidppDevice.MxMaster3s()).FailWrites(MouseEndpoint);
        var route = new HidppBluetoothRoute("MX Master 3S", hid, pingTimeout: FastPing);

        var exception = await Assert.ThrowsAsync<BatteryAdapterException>(() => route.ReadAsync(Route(hid, MouseEndpoint, 0xB034), CancellationToken.None));

        Assert.Equal(BatteryAdapterErrorKind.DeviceOffline, exception.Kind);
        Assert.IsType<IOException>(exception.InnerException);
    }

    [Fact]
    public async Task CancellationIsNotReportedAsOffline()
    {
        var hid = new FakeHidpp().Bluetooth(MouseEndpoint, 0xB034, device: null);
        var route = new HidppBluetoothRoute("MX Master 3S", hid);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => route.ReadAsync(Route(hid, MouseEndpoint, 0xB034), cancellation.Token));
    }

    [Fact]
    public void RouteQueryTargetsTheBluetoothHidppCollection()
    {
        var query = HidppBluetoothRoute.Query(0xB034);

        Assert.Equal((ushort)0x046D, query.VendorId);
        Assert.Equal((ushort)0xB034, query.ProductId);
        Assert.Null(query.InterfaceNumber);
        Assert.Equal((ushort)0xFF43, query.UsagePage);
        Assert.Equal((ushort)0x0202, query.Usage);
        Assert.Equal((byte)0x11, query.RequiredReportId);
    }

    [Fact]
    public void BluetoothLowEnergyHidPathsAreParsedAndOnlyTheUsbVendorSourceIsAccepted()
    {
        var identity = Batterio.App.Windows.WindowsHidCollectionProvider.ParseIdentity(
            @"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&02046d_pid&b034_rev&0006_d06e9b62da62&col02#9&149d8fc1&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}");

        Assert.Equal(((ushort)0x046D, (ushort)0xB034, (ushort?)null), identity);
        Assert.Equal(((ushort)0x046D, (ushort)0xC548, (ushort?)2), Batterio.App.Windows.WindowsHidCollectionProvider.ParseIdentity(
            @"\\?\hid#vid_046d&pid_c548&mi_02&col02#7&1edd123a&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}"));

        // Source 01 is a Bluetooth SIG company ID, not a USB vendor ID: its digits must not be mistaken for one.
        Assert.Null(Batterio.App.Windows.WindowsHidCollectionProvider.ParseIdentity(
            @"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&01046d_pid&b034_rev&0006_d06e9b62da62&col02#9&149d8fc1&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}"));
    }

    [Fact]
    public void TheBluetoothAddressIsReadFromTheHidPath()
    {
        Assert.True(HidCollectionPaths.TryParseBluetoothAddress(
            @"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&02046d_pid&b034_rev&0006_d06e9b62da62&col02#9&149d8fc1&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}",
            out var address));
        Assert.Equal(0xD06E9B62DA62UL, address);
        Assert.False(HidCollectionPaths.TryParseBluetoothAddress(
            @"\\?\hid#vid_046d&pid_c548&mi_02&col02#7&1edd123a&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}", out _));
    }

    [Fact]
    public void SiblingCollectionsOfOneInterfaceShareAKey()
    {
        var long02 = HidCollectionPaths.InterfaceKey(@"\\?\hid#vid_046d&pid_c548&mi_02&col02#7&1edd123a&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}");
        var short01 = HidCollectionPaths.InterfaceKey(@"\\?\HID#VID_046D&PID_C548&MI_02&COL01#7&1EDD123A&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}");
        var otherInterface = HidCollectionPaths.InterfaceKey(@"\\?\hid#vid_046d&pid_c548&mi_01&col01#7&4fa2b88&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}");

        Assert.Equal(long02, short01, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(long02, otherInterface, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProfilesMatchByModelIdOrName()
    {
        var mouse = LogitechMxMaster3s.Identity;
        var keyboard = LogitechMxKeysMechanical.Identity;

        Assert.True(mouse.Matches("anything", new ushort[] { 0xB034 }));
        Assert.True(mouse.Matches("MX Master 3S", Array.Empty<ushort>()));
        Assert.False(mouse.Matches("MX Anywhere 3S", new ushort[] { 0xB037 }));
        Assert.True(keyboard.Matches(" mx mechanical ", new ushort[] { 0x1234 }));
        Assert.True(keyboard.Matches(null, new ushort[] { 0xB366 }));
    }

    private static ResolvedRoute Route(FakeHidpp hid, string path, ushort productId) =>
        new("device", DeviceRouteKind.HidppBluetooth, path, null, RouteState.Unknown,
            hid.Enumerate(HidppBluetoothRoute.Query(productId)).Single(collection => collection.DevicePath == path));
}

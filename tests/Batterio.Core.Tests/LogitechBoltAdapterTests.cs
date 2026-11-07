using System.Collections.Concurrent;
using System.Text;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

public sealed class LogitechBoltAdapterTests
{
    private const byte Pct = 3; // Unified Battery capabilities flag: percentage supported.

    [Fact]
    public async Task MxMaster3sIsFoundOnItsSlotWhileAnotherDeviceInSlotOneIsSkipped()
    {
        var bolt = new FakeBolt()
            .Add("r1", 1, FakeDevice.Other())
            .Add("r1", 2, FakeDevice.MxMaster3s(percentage: 64, unifiedStatus: 0x00));
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s, bolt);

        var reading = await adapter.GetBatteryStatus();

        Assert.Equal(64, reading.Percentage);
        Assert.Equal(BatteryChargeState.NotCharging, reading.ChargeState);
        var writes = bolt.Writes.ToArray();
        // First request: ping of slot 1 (long report, ROOT feature, function 1, software ID 1).
        Assert.Equal(new byte[] { 0x11, 0x01, 0x00, 0x11 }, writes[0].Report[..4]);
        Assert.All(writes, write => Assert.Equal(0x11, write.Report[0]));
        Assert.Contains(writes, write => write.Report[1] == 2 && write.Report[2] == 0 &&
                                         write.Report[3] == 0x01 && write.Report[4] == 0x10 && write.Report[5] == 0x04);
        // The Unified Battery status request goes to slot 2: feature index 3, function 1.
        Assert.Contains(writes, write => write.Report[1] == 2 && write.Report[2] == FakeDevice.UnifiedIndex && write.Report[3] == 0x11);
        Assert.DoesNotContain(writes, write => write.Report[1] == 1 && write.Report[2] == FakeDevice.UnifiedIndex);
    }

    [Fact]
    public async Task ChargingStatusAndExternalPowerAreDecoded()
    {
        var bolt = new FakeBolt().Add("r1", 1, FakeDevice.MxMaster3s(percentage: 99, unifiedStatus: 0x03, externalPower: 1));
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s, bolt);

        var reading = await adapter.GetBatteryStatus();

        Assert.Equal(99, reading.Percentage);
        Assert.Equal(BatteryChargeState.Charging, reading.ChargeState);
    }

    [Fact]
    public async Task MxKeysMechanicalFallsBackToBatteryStatusWhenUnifiedBatteryIsAbsent()
    {
        var bolt = new FakeBolt().Add("r1", 3, FakeDevice.MxMechanical(percentage: 80, legacyStatus: 0x01, unified: false));
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxKeysMechanical, bolt);

        var reading = await adapter.GetBatteryStatus();

        Assert.Equal(80, reading.Percentage);
        Assert.Equal(BatteryChargeState.Charging, reading.ChargeState);
        Assert.Contains(bolt.Writes, write => write.Report[1] == 3 && write.Report[2] == FakeDevice.LegacyIndex);
    }

    [Fact]
    public async Task BatteryStatusErrorCodesAreReportedAsBatteryErrors()
    {
        var bolt = new FakeBolt().Add("r1", 1, FakeDevice.MxMechanical(percentage: 50, legacyStatus: 0x06, unified: false));
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxKeysMechanical, bolt);

        var exception = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());
        Assert.Equal(BatteryAdapterErrorKind.BatteryReportedError, exception.Kind);
    }

    [Fact]
    public async Task DeviceIsIdentifiedByNameWhenTheModelIdIsUnknown()
    {
        var bolt = new FakeBolt().Add("r1", 1, FakeDevice.MxMechanical(percentage: 40, modelId: 0x1234));
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxKeysMechanical, bolt);

        Assert.Equal(40, (await adapter.GetBatteryStatus()).Percentage);
    }

    [Fact]
    public async Task DeviceIsIdentifiedByModelIdWhenTheNameFeatureIsAbsent()
    {
        var bolt = new FakeBolt().Add("r1", 1, FakeDevice.MxMaster3s(percentage: 33) with { Name = null });
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s, bolt);

        Assert.Equal(33, (await adapter.GetBatteryStatus()).Percentage);
    }

    [Fact]
    public async Task DeviceWithNeitherMatchingSignalIsNotAccepted()
    {
        var bolt = new FakeBolt().Add("r1", 1, FakeDevice.MxMaster3s(percentage: 33, modelId: 0x1234) with { Name = "MX Anywhere 3S" });
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s, bolt);

        var exception = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());
        Assert.Equal(BatteryAdapterErrorKind.DeviceOffline, exception.Kind);
    }

    [Fact]
    public async Task DeviceOnTheSecondReceiverIsFoundWithoutAmbiguity()
    {
        var bolt = new FakeBolt()
            .Add("r1", 1, FakeDevice.MxMechanical(percentage: 10))
            .Add("r2", 2, FakeDevice.MxMaster3s(percentage: 71));
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s, bolt);

        Assert.Equal(71, (await adapter.GetBatteryStatus()).Percentage);
        Assert.Equal(new[] { "r1", "r2" }, bolt.OpenedPaths);
    }

    [Fact]
    public async Task ConfiguredDevicePathRestrictsTheScanToThatReceiver()
    {
        var bolt = new FakeBolt()
            .Add("r1", 1, FakeDevice.MxMaster3s(percentage: 12))
            .Add("r2", 2, FakeDevice.MxMaster3s(percentage: 71));
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s, bolt, devicePath: "R2");

        Assert.Equal(71, (await adapter.GetBatteryStatus()).Percentage);
        Assert.Equal(new[] { "r2" }, bolt.OpenedPaths);

        await using var missing = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s, bolt, devicePath: "r3");
        var exception = await Assert.ThrowsAsync<BatteryAdapterException>(() => missing.GetBatteryStatus());
        Assert.Equal(BatteryAdapterErrorKind.ReceiverMissing, exception.Kind);
    }

    [Fact]
    public async Task MissingReceiverIsReportedAsReceiverMissing()
    {
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s, new FakeBolt());

        var exception = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());
        Assert.Equal(BatteryAdapterErrorKind.ReceiverMissing, exception.Kind);
    }

    [Fact]
    public async Task ReceiverWithoutAMatchingDeviceIsReportedAsOffline()
    {
        var bolt = new FakeBolt().Add("r1", 1, FakeDevice.Other()).EmptyReceiver("r2");
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s, bolt);

        var exception = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());
        Assert.Equal(BatteryAdapterErrorKind.DeviceOffline, exception.Kind);
    }

    [Fact]
    public async Task ReceiverCollectionsThatDoNotMatchTheQueryAreIgnored()
    {
        var bolt = new FakeBolt().Add("r1", 1, FakeDevice.MxMaster3s(percentage: 50));
        bolt.Collection("r1", c => c with { InterfaceNumber = 1 });
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s, bolt);

        var exception = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());
        Assert.Equal(BatteryAdapterErrorKind.ReceiverMissing, exception.Kind);
    }

    [Fact]
    public async Task CachedSlotIsProbedFirstAndDroppedAfterAFailure()
    {
        var bolt = new FakeBolt().Add("r1", 4, FakeDevice.MxMaster3s(percentage: 55));
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s, bolt);

        await adapter.GetBatteryStatus();
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, ProbedSlots(bolt));

        bolt.ClearWrites();
        Assert.Equal(55, (await adapter.GetBatteryStatus()).Percentage);
        Assert.Equal(new byte[] { 4 }, ProbedSlots(bolt));

        bolt.Remove("r1", 4);
        bolt.ClearWrites();
        await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());
        Assert.Equal(new byte[] { 4, 1, 2, 3, 5, 6 }, ProbedSlots(bolt));

        bolt.Add("r1", 4, FakeDevice.MxMaster3s(percentage: 56));
        bolt.ClearWrites();
        Assert.Equal(56, (await adapter.GetBatteryStatus()).Percentage);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, ProbedSlots(bolt));
    }

    [Fact]
    public async Task CachedReceiverIsScannedBeforeOthers()
    {
        var bolt = new FakeBolt()
            .Add("r1", 1, FakeDevice.Other())
            .Add("r2", 2, FakeDevice.MxMaster3s(percentage: 71));
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s, bolt);

        await adapter.GetBatteryStatus();
        bolt.ClearOpened();
        await adapter.GetBatteryStatus();

        Assert.Equal(new[] { "r2" }, bolt.OpenedPaths);
    }

    [Fact]
    public async Task BluetoothConnectionIsReadDirectlyBeforeAnyReceiverIsScanned()
    {
        var bolt = new FakeBolt()
            .Bluetooth("bt-b034", 0xB034, FakeDevice.MxMaster3s(percentage: 20))
            .Add("r1", 2, FakeDevice.MxMaster3s(percentage: 90));
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s, bolt);

        var reading = await adapter.GetBatteryStatus();

        Assert.Equal(20, reading.Percentage);
        Assert.Equal(new[] { "bt-b034" }, bolt.OpenedPaths);
        Assert.All(bolt.Writes, write => Assert.Equal(LogitechBoltAdapter.DirectDeviceIndex, write.Report[1]));
    }

    [Fact]
    public async Task BluetoothEndpointOfAnotherProductIsIgnored()
    {
        var bolt = new FakeBolt().Bluetooth("bt-b034", 0xB034, FakeDevice.MxMaster3s(percentage: 20));
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxKeysMechanical, bolt);

        var exception = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());
        Assert.Equal(BatteryAdapterErrorKind.ReceiverMissing, exception.Kind);
        Assert.Empty(bolt.OpenedPaths);
    }

    [Fact]
    public async Task MxMechanicalOverBluetoothReportsCharging()
    {
        var bolt = new FakeBolt().Bluetooth("bt-b366", 0xB366, FakeDevice.MxMechanical(percentage: 65) with { UnifiedStatus = 0x01, ExternalPower = 1 });
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxKeysMechanical, bolt);

        var reading = await adapter.GetBatteryStatus();

        Assert.Equal(65, reading.Percentage);
        Assert.Equal(BatteryChargeState.Charging, reading.ChargeState);
    }

    [Fact]
    public async Task SilentBluetoothEndpointFallsBackToTheBoltReceiver()
    {
        var bolt = new FakeBolt()
            .Bluetooth("bt-b034", 0xB034, device: null)
            .Add("r1", 1, FakeDevice.MxMaster3s(percentage: 44));
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s, bolt);

        Assert.Equal(44, (await adapter.GetBatteryStatus()).Percentage);
        Assert.Equal(new[] { "bt-b034", "r1" }, bolt.OpenedPaths);
    }

    [Fact]
    public async Task ReceiverErrorsOnTheShortCollectionSkipEmptySlotsWithoutWaiting()
    {
        const string longPath = @"\\?\hid#vid_046d&pid_c548&mi_02&col02#7&1edd123a&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        const string shortPath = @"\\?\hid#vid_046d&pid_c548&mi_02&col01#7&1edd123a&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        var bolt = new FakeBolt()
            .Add(longPath, 6, FakeDevice.MxMaster3s(percentage: 58))
            .ShortSibling(longPath, shortPath);
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s, bolt);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var reading = await adapter.GetBatteryStatus();

        Assert.Equal(58, reading.Percentage);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, ProbedSlots(bolt));
        Assert.Contains(shortPath, bolt.OpenedPaths);
        // Five empty slots would take 5 x 750 ms if their errors were only detected by timeout.
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), $"Scan took {started.Elapsed}.");
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
    public void BluetoothLowEnergyHidPathsAreParsed()
    {
        var identity = Batterio.App.Windows.WindowsHidCollectionProvider.ParseIdentity(
            @"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&02046d_pid&b034_rev&0006_d06e9b62da62&col02#9&149d8fc1&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}");

        Assert.Equal(((ushort)0x046D, (ushort)0xB034, (ushort?)null), identity);
        Assert.Equal(((ushort)0x046D, (ushort)0xC548, (ushort?)2), Batterio.App.Windows.WindowsHidCollectionProvider.ParseIdentity(
            @"\\?\hid#vid_046d&pid_c548&mi_02&col02#7&1edd123a&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}"));
    }

    [Fact]
    public async Task BluetoothEnumerationFailureStillScansTheBoltReceiver()
    {
        var bolt = new FakeBolt()
            .Add("r1", 2, FakeDevice.MxMaster3s(percentage: 37))
            .FailEnumeration(query => query.UsagePage == 0xFF43);
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s, bolt);

        Assert.Equal(37, (await adapter.GetBatteryStatus()).Percentage);
    }

    [Fact]
    public async Task ReceiverEnumerationFailureStillReadsTheBluetoothEndpoint()
    {
        var bolt = new FakeBolt()
            .Bluetooth("bt-b034", 0xB034, FakeDevice.MxMaster3s(percentage: 20))
            .FailEnumeration(query => query.UsagePage == 0xFF00);
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s, bolt);

        Assert.Equal(20, (await adapter.GetBatteryStatus()).Percentage);
    }

    [Fact]
    public async Task EnumerationFailureIsReportedWhenNoTransportYieldsAReading()
    {
        var everywhere = new FakeBolt()
            .Bluetooth("bt-b034", 0xB034, FakeDevice.MxMaster3s(percentage: 20))
            .FailEnumeration(_ => true);
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s, everywhere);
        var exception = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());
        Assert.Equal(BatteryAdapterErrorKind.AccessFailure, exception.Kind);

        // The Bluetooth query fails and the only receiver has no matching device: the failure is kept over "offline".
        var partial = new FakeBolt()
            .Add("r1", 1, FakeDevice.Other())
            .FailEnumeration(query => query.UsagePage == 0xFF43);
        await using var partialAdapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s, partial);
        exception = await Assert.ThrowsAsync<BatteryAdapterException>(() => partialAdapter.GetBatteryStatus());
        Assert.Equal(BatteryAdapterErrorKind.AccessFailure, exception.Kind);
    }

    [Fact]
    public async Task BluetoothIoFailureFallsBackToTheBoltReceiver()
    {
        var bolt = new FakeBolt()
            .Bluetooth("bt-b034", 0xB034, FakeDevice.MxMaster3s(percentage: 20))
            .FailWrites("bt-b034")
            .Add("r1", 1, FakeDevice.MxMaster3s(percentage: 44));
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s, bolt);

        Assert.Equal(44, (await adapter.GetBatteryStatus()).Percentage);
        Assert.Equal(new[] { "bt-b034", "r1" }, bolt.OpenedPaths);
    }

    [Fact]
    public async Task BluetoothIoFailureWithoutAnotherTransportIsReportedAsAccessFailure()
    {
        var bolt = new FakeBolt()
            .Bluetooth("bt-b034", 0xB034, FakeDevice.MxMaster3s(percentage: 20))
            .FailWrites("bt-b034");
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s, bolt);

        var exception = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());
        Assert.Equal(BatteryAdapterErrorKind.AccessFailure, exception.Kind);
        Assert.IsType<IOException>(exception.InnerException);
    }

    [Fact]
    public async Task ReceiverIoFailureMovesOnToTheNextReceiver()
    {
        var bolt = new FakeBolt()
            .Add("r1", 1, FakeDevice.MxMaster3s(percentage: 10))
            .FailWrites("r1")
            .Add("r2", 2, FakeDevice.MxMaster3s(percentage: 71));
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s, bolt);

        Assert.Equal(71, (await adapter.GetBatteryStatus()).Percentage);
        Assert.Equal(new[] { "r1", "r2" }, bolt.OpenedPaths);
    }

    [Fact]
    public async Task ShortCollectionIsDisposedWhenCancelledWhileStartingItsTransaction()
    {
        const string longPath = @"\\?\hid#vid_046d&pid_c548&mi_02&col02#7&1edd123a&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        const string shortPath = @"\\?\hid#vid_046d&pid_c548&mi_02&col01#7&1edd123a&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        using var cancellation = new CancellationTokenSource();
        var bolt = new FakeBolt()
            .Add(longPath, 1, FakeDevice.MxMaster3s(percentage: 58))
            .ShortSibling(longPath, shortPath)
            .CancelOnBeginTransaction(shortPath, cancellation);
        await using var adapter = new LogitechBoltAdapter(LogitechBoltDeviceProfile.MxMaster3s, bolt);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetBatteryStatus(cancellation.Token));

        Assert.Contains(shortPath, bolt.OpenedPaths);
        Assert.Contains(shortPath, bolt.DisposedPaths);
    }

    private static byte[] ProbedSlots(FakeBolt bolt) => bolt.Writes
        .Where(write => write.Report[2] == 0 && write.Report[3] == 0x11)
        .Select(write => write.Report[1])
        .ToArray();

    private sealed record FakeDevice(
        string? Name,
        ushort ModelId,
        bool Unified,
        bool Legacy,
        byte Percentage,
        byte UnifiedStatus,
        byte LegacyStatus,
        byte ExternalPower)
    {
        public const byte DeviceInfoIndex = 1;
        public const byte NameIndex = 2;
        public const byte UnifiedIndex = 3;
        public const byte LegacyIndex = 4;

        public static FakeDevice Other() =>
            new("MX Anywhere 3S", 0xB037, true, false, 20, 0, 0, 0);

        public static FakeDevice MxMaster3s(
            byte percentage = 50, byte unifiedStatus = 0, byte externalPower = 0, ushort modelId = 0xB034) =>
            new("MX Master 3S", modelId, true, false, percentage, unifiedStatus, 0, externalPower);

        public static FakeDevice MxMechanical(
            byte percentage = 50, byte legacyStatus = 0, bool unified = true, ushort modelId = 0xB366) =>
            new("MX Mechanical", modelId, unified, !unified, percentage, 0, legacyStatus, 0);

        public byte IndexOf(ushort feature) => feature switch
        {
            0x0003 => DeviceInfoIndex,
            0x0005 when Name is not null => NameIndex,
            0x1004 when Unified => UnifiedIndex,
            0x1000 when Legacy => LegacyIndex,
            _ => 0
        };
    }

    private sealed record Write(string Path, byte[] Report);

    private sealed class FakeBolt : IHidCollectionProvider
    {
        private readonly Dictionary<string, Dictionary<byte, FakeDevice>> _slots = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HidCollectionInfo> _collections = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<Write> _writes = new();
        private readonly ConcurrentQueue<string> _opened = new();
        private readonly Dictionary<string, string> _shortSiblingOf = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _silent = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> _disposed = new();
        private readonly HashSet<string> _failingWrites = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CancellationTokenSource> _cancelOnBegin = new(StringComparer.OrdinalIgnoreCase);
        private Func<HidCollectionQuery, bool> _failingEnumeration = _ => false;

        public IEnumerable<Write> Writes => _writes.ToArray();
        public string[] OpenedPaths => _opened.ToArray();
        public string[] DisposedPaths => _disposed.ToArray();

        /// <summary>Enumeration of matching queries fails as when Windows cannot inspect a collection's descriptors.</summary>
        public FakeBolt FailEnumeration(Func<HidCollectionQuery, bool> predicate)
        {
            _failingEnumeration = predicate;
            return this;
        }

        /// <summary>Writes to the collection fail with an I/O error, as when the device disappears mid-request.</summary>
        public FakeBolt FailWrites(string path)
        {
            _failingWrites.Add(path);
            return this;
        }

        /// <summary>Cancels <paramref name="source"/> while a transaction on the collection is being started.</summary>
        public FakeBolt CancelOnBeginTransaction(string path, CancellationTokenSource source)
        {
            _cancelOnBegin[path] = source;
            return this;
        }

        public FakeBolt EmptyReceiver(string path)
        {
            EnsureReceiver(path);
            return this;
        }

        public FakeBolt Add(string path, byte slot, FakeDevice device)
        {
            EnsureReceiver(path)[slot] = device;
            return this;
        }

        /// <summary>A device connected over Bluetooth LE: its HID++ collection answers on device index 0xFF.</summary>
        public FakeBolt Bluetooth(string path, ushort productId, FakeDevice? device)
        {
            _slots[path] = device is null ? new Dictionary<byte, FakeDevice>() : new Dictionary<byte, FakeDevice> { [0xFF] = device };
            _silent.Add(path);
            _collections[path] = new HidCollectionInfo
            {
                DevicePath = path,
                VendorId = 0x046D,
                ProductId = productId,
                UsagePage = 0xFF43,
                Usage = 0x0202,
                InputReportLength = 20,
                OutputReportLength = 20,
                SupportedReportIds = new HashSet<byte> { 0x11 }
            };
            return this;
        }

        /// <summary>
        /// Adds the receiver's short-report sibling collection. As on Windows, receiver error replies to requests written to the
        /// long collection then arrive only on the short one.
        /// </summary>
        public FakeBolt ShortSibling(string longPath, string shortPath)
        {
            _shortSiblingOf[longPath] = shortPath;
            _collections[shortPath] = new HidCollectionInfo
            {
                DevicePath = shortPath,
                VendorId = 0x046D,
                ProductId = 0xC548,
                InterfaceNumber = 2,
                UsagePage = 0xFF00,
                Usage = 0x0001,
                InputReportLength = 7,
                OutputReportLength = 7,
                SupportedReportIds = new HashSet<byte> { 0x10 }
            };
            return this;
        }

        public void Remove(string path, byte slot) => _slots[path].Remove(slot);

        public void ClearWrites() => _writes.Clear();

        public void ClearOpened() => _opened.Clear();

        public void Collection(string path, Func<HidCollectionInfo, HidCollectionInfo> change) =>
            _collections[path] = change(_collections[path]);

        private Dictionary<byte, FakeDevice> EnsureReceiver(string path)
        {
            if (!_slots.TryGetValue(path, out var slots))
            {
                _slots[path] = slots = new Dictionary<byte, FakeDevice>();
                _collections[path] = new HidCollectionInfo
                {
                    DevicePath = path,
                    VendorId = 0x046D,
                    ProductId = 0xC548,
                    InterfaceNumber = 2,
                    UsagePage = 0xFF00,
                    InputReportLength = 20,
                    OutputReportLength = 20,
                    SupportedReportIds = new HashSet<byte> { 0x11 }
                };
            }

            return slots;
        }

        public IReadOnlyList<HidCollectionInfo> Enumerate(HidCollectionQuery query) => _failingEnumeration(query)
            ? throw new BatteryAdapterException(BatteryAdapterErrorKind.AccessFailure, "Could not inspect HID descriptors.")
            : _collections.Values.ToArray();

        public ValueTask<IHidReportTransport> OpenAsync(HidCollectionInfo collection, CancellationToken cancellationToken = default)
        {
            _opened.Enqueue(collection.DevicePath);
            var session = new Session(this, collection);
            _sessions[collection.DevicePath] = session;
            return ValueTask.FromResult<IHidReportTransport>(session);
        }

        private sealed class Session(FakeBolt owner, HidCollectionInfo collection) : IHidReportTransport, IHidReportTransaction
        {
            private readonly ConcurrentQueue<byte[]> _replies = new();
            private readonly SemaphoreSlim _available = new(0);
            public HidCollectionInfo Collection => collection;

            public ValueTask<IHidReportTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
            {
                if (owner._cancelOnBegin.TryGetValue(collection.DevicePath, out var source))
                {
                    source.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return ValueTask.FromResult<IHidReportTransaction>(this);
            }

            public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default)
            {
                if (owner._failingWrites.Contains(collection.DevicePath))
                {
                    return ValueTask.FromException(new IOException("The device is not connected."));
                }

                var bytes = report.ToArray();
                owner._writes.Enqueue(new Write(collection.DevicePath, bytes));
                var reply = owner.Reply(collection.DevicePath, bytes);
                if (reply is null)
                {
                    return ValueTask.CompletedTask;
                }

                if (reply[2] == 0x8F && owner._shortSiblingOf.TryGetValue(collection.DevicePath, out var shortPath))
                {
                    // Delivered as a short report on the sibling collection, or lost when nobody reads it.
                    if (owner._sessions.TryGetValue(shortPath, out var sibling))
                    {
                        var shortReply = reply[..7];
                        shortReply[0] = 0x10;
                        sibling.Enqueue(shortReply);
                    }
                    return ValueTask.CompletedTask;
                }

                Enqueue(reply);
                return ValueTask.CompletedTask;
            }

            public void Enqueue(byte[] reply)
            {
                _replies.Enqueue(reply);
                _available.Release();
            }

            public async ValueTask<int> ReadAsync(Memory<byte> report, CancellationToken cancellationToken = default)
            {
                await _available.WaitAsync(cancellationToken);
                if (!_replies.TryDequeue(out var reply)) throw new InvalidOperationException();
                reply.CopyTo(report);
                return reply.Length;
            }

            public ValueTask DisposeAsync()
            {
                owner._disposed.Enqueue(collection.DevicePath);
                return ValueTask.CompletedTask;
            }
        }

        private byte[]? Reply(string path, byte[] request)
        {
            var response = new byte[20];
            response[0] = 0x11;
            response[1] = request[1];
            response[2] = request[2];
            response[3] = request[3];

            if (!_slots[path].TryGetValue(request[1], out var device))
            {
                if (_silent.Contains(path)) return null; // A sleeping Bluetooth device simply does not answer.

                // HID++ 1.0 receiver error: unknown device. Reported through the long collection here to keep tests fast.
                response[2] = 0x8F;
                response[3] = request[2];
                response[4] = request[3];
                response[5] = 0x08;
                return response;
            }

            var feature = request[2];
            var function = request[3] & 0xF0;
            if (feature == 0)
            {
                if (function == 0x10)
                {
                    response[4] = 4;
                    response[5] = 2;
                    response[6] = request[6];
                }
                else
                {
                    response[4] = device.IndexOf((ushort)((request[4] << 8) | request[5]));
                }
            }
            else if (feature == FakeDevice.DeviceInfoIndex)
            {
                response[4] = 3;
                response[10] = 0x02; // BLE transport only (Logi Bolt devices talk BLE to the receiver).
                response[11] = (byte)(device.ModelId >> 8);
                response[12] = (byte)device.ModelId;
            }
            else if (feature == FakeDevice.NameIndex)
            {
                var name = Encoding.UTF8.GetBytes(device.Name!);
                if (function == 0)
                {
                    response[4] = (byte)name.Length;
                }
                else
                {
                    var offset = request[4];
                    name.AsSpan(offset, Math.Min(16, name.Length - offset)).CopyTo(response.AsSpan(4));
                }
            }
            else if (feature == FakeDevice.UnifiedIndex)
            {
                if (function == 0)
                {
                    response[4] = 15;
                    response[5] = Pct;
                }
                else
                {
                    response[4] = device.Percentage;
                    response[5] = 8;
                    response[6] = device.UnifiedStatus;
                    response[7] = device.ExternalPower;
                }
            }
            else if (feature == FakeDevice.LegacyIndex)
            {
                response[4] = device.Percentage;
                response[5] = 20;
                response[6] = device.LegacyStatus;
            }

            return response;
        }
    }
}

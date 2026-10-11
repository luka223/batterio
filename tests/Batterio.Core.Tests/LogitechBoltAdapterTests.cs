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

        public IEnumerable<Write> Writes => _writes.ToArray();
        public string[] OpenedPaths => _opened.ToArray();

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

        public IReadOnlyList<HidCollectionInfo> Enumerate(HidCollectionQuery query) => _collections.Values.ToArray();

        public ValueTask<IHidReportTransport> OpenAsync(HidCollectionInfo collection, CancellationToken cancellationToken = default)
        {
            _opened.Enqueue(collection.DevicePath);
            return ValueTask.FromResult<IHidReportTransport>(new Session(this, collection));
        }

        private sealed class Session(FakeBolt owner, HidCollectionInfo collection) : IHidReportTransport, IHidReportTransaction
        {
            private readonly ConcurrentQueue<byte[]> _replies = new();
            public HidCollectionInfo Collection => collection;

            public ValueTask<IHidReportTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
                ValueTask.FromResult<IHidReportTransaction>(this);

            public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default)
            {
                var bytes = report.ToArray();
                owner._writes.Enqueue(new Write(collection.DevicePath, bytes));
                _replies.Enqueue(owner.Reply(collection.DevicePath, bytes));
                return ValueTask.CompletedTask;
            }

            public async ValueTask<int> ReadAsync(Memory<byte> report, CancellationToken cancellationToken = default)
            {
                if (!_replies.TryDequeue(out var reply))
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    throw new InvalidOperationException();
                }

                reply.CopyTo(report);
                return reply.Length;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private byte[] Reply(string path, byte[] request)
        {
            var response = new byte[20];
            response[0] = 0x11;
            response[1] = request[1];
            response[2] = request[2];
            response[3] = request[3];

            if (!_slots[path].TryGetValue(request[1], out var device))
            {
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

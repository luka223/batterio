using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

public sealed class LogiBoltReceiverTests
{
    private const string Long = FakeHidpp.ReceiverLongPath;
    private const string Short = FakeHidpp.ReceiverShortPath;

    internal static readonly LogiBoltReceiverOptions FastOptions = new()
    {
        NoticeTimeout = TimeSpan.FromMilliseconds(200),
        NoticeSettleDelay = TimeSpan.FromMilliseconds(30),
        PingTimeout = TimeSpan.FromMilliseconds(150),
        RequestTimeout = TimeSpan.FromSeconds(2),
        RegisterTimeout = TimeSpan.FromMilliseconds(300)
    };

    [Fact]
    public async Task ConnectionNoticesFillThePairingTableWithoutWakingAnyDevice()
    {
        var hid = new FakeHidpp().Receiver()
            .Add(Long, 2, FakeHidppDevice.MxMaster3s(percentage: 64))
            .Add(Long, 4, FakeHidppDevice.MxMechanical(), online: false);
        await using var receiver = await OpenAsync(hid);

        Assert.Equal(LogiBoltPairingSource.Notices, receiver.Source);
        var slots = receiver.Slots;
        Assert.Equal(2, slots.Count);
        Assert.Equal((byte)2, slots[0].Slot);
        Assert.Equal((ushort)0xB034, slots[0].Wpid);
        Assert.True(slots[0].Online);
        Assert.Equal(new ushort[] { 0xB034 }, slots[0].ModelIds);
        Assert.Equal(LogiBoltPairingSource.Notices, slots[0].Source);
        Assert.Equal((ushort)0xB366, slots[1].Wpid);
        Assert.False(slots[1].Online);
        // No HID++ 2.0 request (ROOT feature, function 1/0) went to any device: only receiver register traffic.
        Assert.All(hid.Writes, write => Assert.Equal(0xFF, write.Report[1]));
    }

    [Fact]
    public async Task NotificationFlagsAreWrittenOnlyWhenTheWirelessBitIsMissing()
    {
        var missing = new FakeHidpp().Receiver().SetFlags(Long, new byte[] { 0x10, 0x08, 0x04 })
            .Add(Long, 1, FakeHidppDevice.MxMaster3s());
        await using (await OpenAsync(missing))
        {
            // Read 0x81/0x00, then write 0x80/0x00 keeping the other bits and adding wireless (0x000100).
            var write = Assert.Single(missing.Writes, w => w.Report[2] == 0x80 && w.Report[3] == 0x00);
            Assert.Equal(new byte[] { 0x10, 0xFF, 0x80, 0x00, 0x10, 0x09, 0x04 }, write.Report);
            Assert.Equal(Short, write.Path);
        }

        var enabled = new FakeHidpp().Receiver().SetFlags(Long, new byte[] { 0x00, 0x01, 0x00 })
            .Add(Long, 1, FakeHidppDevice.MxMaster3s());
        await using (await OpenAsync(enabled))
        {
            Assert.DoesNotContain(enabled.Writes, w => w.Report[2] == 0x80 && w.Report[3] == 0x00);
            Assert.Contains(enabled.Writes, w => w.Report[2] == 0x81 && w.Report[3] == 0x00);
        }
    }

    [Fact]
    public async Task ArrivalOfEveryPairedDeviceIsRequestedWithTheDocumentedFrame()
    {
        var hid = new FakeHidpp().Receiver().Add(Long, 1, FakeHidppDevice.MxMaster3s());
        await using var receiver = await OpenAsync(hid);

        var trigger = Assert.Single(hid.Writes, w => w.Report[2] == 0x80 && w.Report[3] == 0x02);
        Assert.Equal(new byte[] { 0x10, 0xFF, 0x80, 0x02, 0x02, 0x00, 0x00 }, trigger.Report);
    }

    [Fact]
    public async Task RegistersAreWrittenAsLongReportsWhenTheShortCollectionIsMissing()
    {
        var hid = new FakeHidpp().Receiver(withShortCollection: false).Add(Long, 1, FakeHidppDevice.MxMaster3s());
        await using var receiver = await OpenAsync(hid);

        Assert.Equal(LogiBoltPairingSource.Notices, receiver.Source);
        var trigger = Assert.Single(hid.Writes, w => w.Report[2] == 0x80 && w.Report[3] == 0x02);
        Assert.Equal(0x11, trigger.Report[0]);
        Assert.Equal(20, trigger.Report.Length);
    }

    [Fact]
    public async Task ExecuteReadsTheBatteryOfOneSlotDirectly()
    {
        var hid = new FakeHidpp().Receiver()
            .Add(Long, 1, FakeHidppDevice.Other())
            .Add(Long, 2, FakeHidppDevice.MxMaster3s(percentage: 64));
        await using var receiver = await OpenAsync(hid);
        hid.ClearWrites();

        var reading = await receiver.ExecuteAsync(2, (client, token) =>
            HidppBatteryReader.ReadAsync(client, 2, "MX Master 3S", Long, "slot 0x02", new NullBatterioLog(), token));

        Assert.Equal(64, reading.Percentage);
        Assert.All(hid.Writes, write => Assert.Equal(2, write.Report[1]));
        Assert.DoesNotContain(hid.Writes, write => write.Report[2] == 0 && write.Report[3] == 0x11); // no ping
    }

    [Fact]
    public async Task NoticesThatArriveDuringARequestAreStillDispatchedAndDoNotDisturbTheRequest()
    {
        var hid = new FakeHidpp().Receiver()
            .Add(Long, 2, FakeHidppDevice.MxMaster3s(percentage: 64))
            .Add(Long, 3, FakeHidppDevice.MxMechanical(percentage: 30));
        await using var receiver = await OpenAsync(hid);
        var disconnected = new List<byte>();
        receiver.DeviceDisconnected += (_, e) => disconnected.Add(e.Slot.Slot);

        var reading = await receiver.ExecuteAsync(2, async (client, token) =>
        {
            hid.Notify(Long, 3, 0xB366, online: false); // the keyboard goes to sleep in the middle of the request
            await Task.Delay(50, token);
            return await HidppBatteryReader.ReadAsync(client, 2, "MX Master 3S", Long, "slot 0x02", new NullBatterioLog(), token);
        });

        Assert.Equal(64, reading.Percentage);
        await Eventually(() => disconnected.Count == 1);
        Assert.Equal(new byte[] { 3 }, disconnected);
        Assert.False(receiver.GetSlot(3)!.Online);
    }

    [Fact]
    public async Task LaterNoticesRaiseConnectDisconnectAndPairingEvents()
    {
        var hid = new FakeHidpp().Receiver().Add(Long, 1, FakeHidppDevice.MxMaster3s(), online: false);
        await using var receiver = await OpenAsync(hid);
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        receiver.DeviceConnected += (_, e) => events.Enqueue($"connected {e.Slot.Slot}");
        receiver.DeviceDisconnected += (_, e) => events.Enqueue($"disconnected {e.Slot.Slot}");
        receiver.PairingChanged += (_, e) => events.Enqueue($"pairing {e.Slot.Slot}");

        hid.Notify(Long, 1, 0xB034, online: true);     // wakes up
        await Eventually(() => events.Count == 1);
        hid.Notify(Long, 1, 0xB034, online: true);     // repeated notice: nothing changes
        hid.Notify(Long, 1, 0xB034, online: false);    // goes to sleep
        await Eventually(() => events.Count == 2);
        hid.Notify(Long, 5, 0xB366, online: true);     // a new device is paired and connects
        await Eventually(() => events.Count == 4);
        hid.NotifyUnpaired(Long, 5);
        await Eventually(() => events.Count == 5);

        Assert.Equal(new[] { "connected 1", "disconnected 1", "pairing 5", "connected 5", "pairing 5" }, events.ToArray());
        Assert.Null(receiver.GetSlot(5));
    }

    [Fact]
    public async Task ConnectionNoticesWithAnUnknownProtocolAreIgnored()
    {
        var hid = new FakeHidpp().Receiver().Add(Long, 1, FakeHidppDevice.MxMaster3s());
        await using var receiver = await OpenAsync(hid);

        var unknownProtocol = FakeHidpp.ConnectionNotice(4, 0xB366, true);
        unknownProtocol[3] = 0x00; // address 0: "unknown protocol" in Solaar
        var oldProtocol = FakeHidpp.ConnectionNotice(5, 0xB366, true);
        oldProtocol[3] = 0x02;     // address 2: the 27 MHz protocol, whose WPID is laid out differently
        hid.NotifyRaw(Long, unknownProtocol);
        hid.NotifyRaw(Long, oldProtocol);
        hid.Notify(Long, 6, 0xB366, true);
        await Eventually(() => receiver.GetSlot(6) is not null);

        Assert.Null(receiver.GetSlot(4));
        Assert.Null(receiver.GetSlot(5));
        Assert.Equal(2, receiver.Slots.Count);
    }

    [Fact]
    public async Task WithoutNoticesTheSlotsAreIdentifiedByAScanAndCached()
    {
        var log = new ListLog();
        var hid = new FakeHidpp().Receiver().WithoutArrivalNotices()
            .Add(Long, 1, FakeHidppDevice.Other())
            .Add(Long, 3, FakeHidppDevice.MxMaster3s(percentage: 71));
        await using var receiver = await OpenAsync(hid, log);

        Assert.Equal(LogiBoltPairingSource.Scan, receiver.Source);
        Assert.Contains(log.Messages, message => message.Contains("no connection notices", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new byte[] { 1, 3 }, receiver.Slots.Select(slot => slot.Slot).ToArray());
        var mouse = receiver.GetSlot(3)!;
        Assert.Equal("MX Master 3S", mouse.Name);
        Assert.Equal(new ushort[] { 0xB034 }, mouse.ModelIds);
        Assert.True(mouse.Matches(0xB034, null));
        Assert.False(receiver.GetSlot(1)!.Matches(0xB034, null));
        // Every slot was probed once, empty ones answered with a receiver error on the short collection.
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, ProbedSlots(hid));

        hid.ClearWrites();
        var reading = await receiver.ExecuteAsync(3, (client, token) =>
            HidppBatteryReader.ReadAsync(client, 3, "MX Master 3S", Long, "slot 0x03", new NullBatterioLog(), token));
        Assert.Equal(71, reading.Percentage);
        Assert.Empty(ProbedSlots(hid)); // the cached table is used; no scan, no identity check
    }

    [Fact]
    public async Task ScanFallbackAlsoWorksWhenTheReceiverRejectsRegisterAccess()
    {
        var hid = new FakeHidpp().Receiver().RejectRegisters().Add(Long, 2, FakeHidppDevice.MxMechanical(percentage: 40));
        await using var receiver = await OpenAsync(hid);

        Assert.Equal(LogiBoltPairingSource.Scan, receiver.Source);
        Assert.True(receiver.GetSlot(2)!.Matches(0x1234, new HashSet<string> { "MX Mechanical" })); // by name when the model ID is unknown
    }

    [Fact]
    public async Task ScanFallbackWorksWhenTheReceiverNeverAnswersRegisterRequests()
    {
        var hid = new FakeHidpp().Receiver().SilentRegisters().Add(Long, 2, FakeHidppDevice.MxMaster3s());
        await using var receiver = await OpenAsync(hid);

        Assert.Equal(LogiBoltPairingSource.Scan, receiver.Source);
        Assert.Single(receiver.Slots);
    }

    [Fact]
    public async Task ASlotThatDoesNotAnswerInTimeIsReportedOffline()
    {
        var hid = new FakeHidpp().Receiver().Add(Long, 2, FakeHidppDevice.MxMaster3s());
        await using var receiver = await OpenAsync(hid, options: FastOptions with { RequestTimeout = TimeSpan.FromMilliseconds(100) });

        var exception = await Assert.ThrowsAsync<BatteryAdapterException>(() => receiver.ExecuteAsync<int>(2, async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }));
        Assert.Equal(BatteryAdapterErrorKind.DeviceOffline, exception.Kind);
        Assert.False(receiver.IsClosed);
    }

    [Fact]
    public async Task AnIoErrorClosesTheReceiverAndItIsNeverRetried()
    {
        var hid = new FakeHidpp().Receiver().Add(Long, 2, FakeHidppDevice.MxMaster3s());
        await using var receiver = await OpenAsync(hid);
        var closed = 0;
        receiver.Closed += (_, _) => Interlocked.Increment(ref closed);

        hid.FailWrites(Long); // the receiver is unplugged
        var exception = await Assert.ThrowsAsync<BatteryAdapterException>(() => receiver.ExecuteAsync(2, (client, token) =>
            HidppBatteryReader.ReadAsync(client, 2, "MX Master 3S", Long, "slot 0x02", new NullBatterioLog(), token)));

        Assert.Equal(BatteryAdapterErrorKind.AccessFailure, exception.Kind);
        Assert.IsType<IOException>(exception.InnerException);
        Assert.True(receiver.IsClosed);
        Assert.Equal(1, closed);

        hid.ClearOpened();
        var again = await Assert.ThrowsAsync<BatteryAdapterException>(() => receiver.ExecuteAsync(2, (_, _) => Task.FromResult(0)));
        Assert.Equal(BatteryAdapterErrorKind.AccessFailure, again.Kind);
        Assert.Empty(hid.OpenedPaths);
    }

    [Fact]
    public async Task OpeningFailureDisposesWhatWasOpened()
    {
        var hid = new FakeHidpp().Receiver().FailWrites(Short).Add(Long, 2, FakeHidppDevice.MxMaster3s());
        var receiver = Create(hid);

        await Assert.ThrowsAsync<BatteryAdapterException>(() => receiver.OpenAsync());

        Assert.Contains(Long, hid.DisposedPaths);
        Assert.Contains(Short, hid.DisposedPaths);
    }

    // --- manager ---

    [Fact]
    public async Task ManagerOpensNewReceiversFindsDevicesAndDisposesRemovedOnes()
    {
        var second = @"\\?\hid#vid_046d&pid_c548&mi_02&col02#7&2aaa0001&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        var hid = new FakeHidpp().Receiver()
            .Add(Long, 2, FakeHidppDevice.MxMaster3s())
            .Receiver(second, withShortCollection: false)
            .Add(second, 5, FakeHidppDevice.MxMechanical());
        await using var manager = new LogiBoltReceiverManager(hid, new NullBatterioLog(), FastOptions);

        await manager.SyncAsync(hid.Enumerate(Query));

        Assert.Equal(2, manager.Receivers.Count);
        var mouse = Assert.Single(manager.FindAll(0xB034));
        Assert.Equal(Long, mouse.Receiver.Path);
        Assert.Equal((byte)2, mouse.Slot.Slot);
        Assert.Equal(second, Assert.Single(manager.FindAll(0xB366)).Receiver.Path);
        Assert.Empty(manager.FindAll(0x1234));

        hid.RemoveCollection(second);
        await manager.SyncAsync(hid.Enumerate(Query));
        Assert.Single(manager.Receivers);
        Assert.Empty(manager.FindAll(0xB366));
        Assert.Contains(second, hid.DisposedPaths);

        await manager.SyncAsync(hid.Enumerate(Query));
        Assert.Equal(1, hid.OpenedPaths.Count(path => path == Long)); // an open receiver is kept, not reopened
    }

    [Fact]
    public async Task ManagerReplacesAReceiverThatFailedOnTheNextSync()
    {
        var hid = new FakeHidpp().Receiver().Add(Long, 2, FakeHidppDevice.MxMaster3s());
        await using var manager = new LogiBoltReceiverManager(hid, new NullBatterioLog(), FastOptions);
        var closed = 0;
        manager.ReceiverClosed += (_, _) => Interlocked.Increment(ref closed);
        await manager.SyncAsync(hid.Enumerate(Query));
        var first = Assert.Single(manager.Receivers);

        hid.FailWrites(Long); // unplugged
        await Assert.ThrowsAsync<BatteryAdapterException>(() => first.ExecuteAsync(2, (client, token) =>
            HidppBatteryReader.ReadAsync(client, 2, "MX Master 3S", Long, "slot 0x02", new NullBatterioLog(), token)));
        Assert.Equal(1, closed);
        Assert.Empty(manager.FindAll(0xB034)); // a closed receiver offers no devices

        hid.RestoreWrites(Long);
        await manager.SyncAsync(hid.Enumerate(Query));

        var replacement = Assert.Single(manager.Receivers);
        Assert.NotSame(first, replacement);
        Assert.Single(manager.FindAll(0xB034));
        Assert.Equal(2, hid.OpenedPaths.Count(path => path == Long));
    }

    [Fact]
    public async Task ManagerLogsAReceiverThatCannotBeOpenedOnceAndRetriesIt()
    {
        var log = new ListLog();
        var hid = new FakeHidpp().Receiver().FailWrites(Short).Add(Long, 2, FakeHidppDevice.MxMaster3s());
        await using var manager = new LogiBoltReceiverManager(hid, log, FastOptions);

        await manager.SyncAsync(hid.Enumerate(Query));
        await manager.SyncAsync(hid.Enumerate(Query));

        Assert.Empty(manager.Receivers);
        Assert.Single(log.Messages, message => message.Contains("could not be opened", StringComparison.Ordinal));
        Assert.Equal(2, hid.OpenedPaths.Count(path => path == Long)); // tried again on the second sync
    }

    [Fact]
    public async Task ManagerForwardsReceiverEventsAfterTheInitialAnnouncements()
    {
        var hid = new FakeHidpp().Receiver().Add(Long, 2, FakeHidppDevice.MxMaster3s(), online: false);
        await using var manager = new LogiBoltReceiverManager(hid, new NullBatterioLog(), FastOptions);
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        manager.DeviceConnected += (_, e) => events.Enqueue($"connected {e.ReceiverPath == Long} {e.Slot.Slot}");
        manager.PairingChanged += (_, e) => events.Enqueue($"pairing {e.Slot.Slot}");

        await manager.SyncAsync(hid.Enumerate(Query));
        Assert.Empty(events); // the arrival burst while opening is not a change
        hid.Notify(Long, 2, 0xB034, online: true);

        await Eventually(() => events.Count == 1);
        Assert.Equal("connected True 2", events.Single());
    }

    private static readonly HidCollectionQuery[] Query =
    [
        LogitechReceiverDefinitions.BoltReceiverQuery,
        LogitechReceiverDefinitions.BoltReceiverShortQuery
    ];

    private static LogiBoltReceiver Create(FakeHidpp hid, IBatterioLog? log = null, LogiBoltReceiverOptions? options = null)
    {
        var collections = hid.Enumerate(new HidCollectionQuery { VendorId = 0x046D, ProductId = 0xC548 });
        var longCollection = collections.Single(c => c.DevicePath == Long);
        var shortCollection = collections.FirstOrDefault(c => c.DevicePath == Short);
        return new LogiBoltReceiver(hid, longCollection, shortCollection, log, options ?? FastOptions);
    }

    private static async Task<LogiBoltReceiver> OpenAsync(FakeHidpp hid, IBatterioLog? log = null, LogiBoltReceiverOptions? options = null)
    {
        var receiver = Create(hid, log, options);
        await receiver.OpenAsync();
        return receiver;
    }

    private static byte[] ProbedSlots(FakeHidpp hid) => hid.Writes
        .Where(write => write.Report[2] == 0 && write.Report[3] == 0x11)
        .Select(write => write.Report[1])
        .ToArray();

    private static async Task Eventually(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        Assert.True(condition());
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

/// <summary>The Omni hub announces the headset connecting and disconnecting; the monitor turns that into the state of the hub's route.</summary>
public sealed class SteelSeriesHubMonitorTests
{
    private const string OmniId = "steelseries-arctis-nova-pro-omni";
    private const string StatusPath = @"\\?\hid#vid_1038&pid_2290&mi_03&col01#a&2a1224a3&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string EventsPath = @"\\?\hid#vid_1038&pid_2290&mi_03&col02#a&2a1224a3&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";

    // Frames recorded on a real hub (report ID 0x07 first), shortened to their meaningful bytes.
    private static readonly byte[] HeadsetOff = Frame(0xB5, 0x01, 0x00, 0x04);
    private static readonly byte[] HeadsetOn = Frame(0xB5, 0x01, 0x00, 0x08);

    [Fact]
    public void OnlyTheConnectionEventIsDecodedAndOnlyForTheTwoKnownStates()
    {
        Assert.True(SteelSeriesNovaProOmniStatusDecoding.TryDecodeConnectionEvent(HeadsetOn, out var connected));
        Assert.True(connected);
        Assert.True(SteelSeriesNovaProOmniStatusDecoding.TryDecodeConnectionEvent(HeadsetOff, out connected));
        Assert.False(connected);

        Assert.False(SteelSeriesNovaProOmniStatusDecoding.TryDecodeConnectionEvent(Frame(0xB5, 0x01, 0x00, 0x00), out _));
        Assert.False(SteelSeriesNovaProOmniStatusDecoding.TryDecodeConnectionEvent(Frame(0xB7, 0x00, 0x64, 0x01), out _));
        Assert.False(SteelSeriesNovaProOmniStatusDecoding.TryDecodeConnectionEvent(Frame(0x80, 0x03, 0x0A, 0x01), out _));
        Assert.False(SteelSeriesNovaProOmniStatusDecoding.TryDecodeConnectionEvent(new byte[] { 0x07, 0xB5, 0x01, 0x00 }, out _));
        var notAnEvent = (byte[])HeadsetOn.Clone();
        notAnEvent[0] = 0x01;
        Assert.False(SteelSeriesNovaProOmniStatusDecoding.TryDecodeConnectionEvent(notAnEvent, out _));
    }

    [Fact]
    public async Task TheRouteFollowsTheHeadsetConnectingAndDisconnecting()
    {
        var hid = new EventProvider();
        await using var monitor = new SteelSeriesHubMonitor(hid);
        var changes = new ConcurrentQueue<RouteState?>();
        monitor.StateChanged += (_, _) => changes.Enqueue(monitor.GetState(Status()));
        await monitor.SyncAsync([Status(), Events()]);
        await Eventually(() => hid.Opens == 1);
        Assert.Equal(RouteState.Unknown, monitor.GetState(Status()));

        hid.Push(Frame(0xB7, 0x61, 0x64, 0x08)); // other events are ignored
        hid.Push(HeadsetOn);
        await Eventually(() => changes.Count == 1);
        hid.Push(HeadsetOn); // a repeat changes nothing
        hid.Push(HeadsetOff);
        await Eventually(() => changes.Count == 2);
        hid.Push(HeadsetOn);
        await Eventually(() => changes.Count == 3);

        Assert.Equal(new RouteState?[] { RouteState.Online, RouteState.Offline, RouteState.Online }, changes.ToArray());
        Assert.Empty(hid.Writes);
    }

    [Fact]
    public async Task AllTheQueriesTheMonitorNeedsAreAskedForAndOnlyTheStatusCollectionHasAState()
    {
        var hid = new EventProvider();
        await using var monitor = new SteelSeriesHubMonitor(hid);
        Assert.Same(SteelSeriesArctisNovaProOmni.EventsQuery, Assert.Single(monitor.Queries));
        Assert.Null(monitor.GetState(Events()));
        Assert.Null(monitor.GetState(Status() with { ProductId = 0x1234 }));

        // Without the event collection nothing can be listened to, and the state stays unknown.
        await monitor.SyncAsync([Status()]);
        Assert.Equal(0, hid.Opens);
        Assert.Equal(RouteState.Unknown, monitor.GetState(Status()));
    }

    [Fact]
    public async Task AReadErrorClosesTheListenerAndTheNextSyncReplacesIt()
    {
        var hid = new EventProvider();
        await using var monitor = new SteelSeriesHubMonitor(hid);
        await monitor.SyncAsync([Status(), Events()]);
        hid.Push(HeadsetOn);
        await Eventually(() => monitor.GetState(Status()) == RouteState.Online);

        hid.Fail(new IOException("the hub was unplugged"));

        await Eventually(() => monitor.GetState(Status()) == RouteState.Unknown);
        hid.Reset();
        await monitor.SyncAsync([Status(), Events()]);
        await Eventually(() => hid.Opens == 2);
        hid.Push(HeadsetOff);
        await Eventually(() => monitor.GetState(Status()) == RouteState.Offline);
    }

    [Fact]
    public async Task UnpluggingTheHubStopsListening()
    {
        var hid = new EventProvider();
        await using var monitor = new SteelSeriesHubMonitor(hid);
        await monitor.SyncAsync([Status(), Events()]);
        await Eventually(() => hid.Opens == 1);

        await monitor.SyncAsync([]);

        Assert.Equal(RouteState.Unknown, monitor.GetState(Status()));
    }

    [Fact]
    public async Task SwitchingTheHeadsetOnReadsItThroughDiscoveryWithoutAnyPolling()
    {
        var hid = new EventProvider { Collections = [Status(), Events()] };
        await using var monitor = new SteelSeriesHubMonitor(hid);
        var routes = new DeviceRouteTable();
        var registry = new AdapterRegistry([new NoopFactory()]);
        await using var discovery = new DeviceDiscoveryService(
            [BuiltInDevices.CatalogById[OmniId]], registry, hid, null, routes, null, null, null,
            new DeviceDiscoveryOptions { FallbackInterval = TimeSpan.FromHours(1) }, [monitor]);
        var cameOnline = new ConcurrentQueue<string>();
        discovery.DeviceCameOnline += (_, e) => cameOnline.Enqueue(e.DeviceId);

        var snapshot = await discovery.DiscoverAsync();
        Assert.True(snapshot.Devices.Single().IsPresent);
        Assert.Equal(RouteState.Unknown, Assert.Single(routes.GetRoutes(OmniId)).State);
        Assert.Contains(EventsPath, hid.EnumeratedPaths); // the event collection was enumerated although it is no route
        await discovery.StartAsync();
        await Eventually(() => hid.Opens == 1);

        hid.Push(HeadsetOff);
        await Eventually(() => routes.GetRoutes(OmniId).Single().State == RouteState.Offline);
        Assert.Empty(cameOnline);

        hid.Push(HeadsetOn);
        await Eventually(() => routes.GetRoutes(OmniId).Single().State == RouteState.Online);
        await Eventually(() => cameOnline.Count == 1);
        Assert.Single(routes.GetRoutes(OmniId)); // the event collection never became a route
    }

    private static byte[] Frame(byte opcode, byte b2, byte b3, byte b4)
    {
        var frame = new byte[64];
        frame[0] = 0x07;
        frame[1] = opcode;
        frame[2] = b2;
        frame[3] = b3;
        frame[4] = b4;
        return frame;
    }

    private static HidCollectionInfo Status() => new()
    {
        DevicePath = StatusPath,
        VendorId = 0x1038,
        ProductId = 0x2290,
        InterfaceNumber = 3,
        UsagePage = 0xFFC0,
        Usage = 0x0001,
        InputReportLength = 64,
        OutputReportLength = 64,
        SupportedReportIds = new HashSet<byte> { 0x01 }
    };

    private static HidCollectionInfo Events() => new()
    {
        DevicePath = EventsPath,
        VendorId = 0x1038,
        ProductId = 0x2290,
        InterfaceNumber = 3,
        UsagePage = 0xFF00,
        Usage = 0x0001,
        InputReportLength = 64,
        OutputReportLength = 64,
        SupportedReportIds = new HashSet<byte> { 0x07 }
    };

    private static async Task Eventually(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class NoopFactory : IBatteryAdapterFactory
    {
        public string AdapterId => OmniId;

        public void ValidateSettings(JsonElement settings) { }

        public IBatteryAdapter CreateAdapter(DeviceConfiguration device) => throw new NotSupportedException();
    }

    /// <summary>A hub whose event collection delivers the frames the test pushes.</summary>
    internal sealed class EventProvider : IHidCollectionProvider
    {
        private Channel<byte[]> _reports = Channel.CreateUnbounded<byte[]>();
        private int _opens;

        public IReadOnlyList<HidCollectionInfo> Collections { get; set; } = [];

        public ConcurrentQueue<string> EnumeratedPathsLog { get; } = new();

        public IEnumerable<string> EnumeratedPaths => EnumeratedPathsLog;

        public int Opens => Volatile.Read(ref _opens);

        public ConcurrentQueue<byte[]> Writes { get; } = new();

        public IReadOnlyList<HidCollectionInfo> Enumerate(HidCollectionQuery query)
        {
            foreach (var collection in Collections)
            {
                EnumeratedPathsLog.Enqueue(collection.DevicePath);
            }

            return Collections;
        }

        public void Push(byte[] report) => _reports.Writer.TryWrite(report);

        public void Fail(Exception exception) => _reports.Writer.TryComplete(exception);

        public void Reset() => _reports = Channel.CreateUnbounded<byte[]>();

        public ValueTask<IHidReportTransport> OpenAsync(HidCollectionInfo collection, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _opens);
            return ValueTask.FromResult<IHidReportTransport>(new Transport(this, collection, _reports));
        }

        private sealed class Transport(EventProvider owner, HidCollectionInfo collection, Channel<byte[]> reports)
            : IHidReportTransport, IHidReportTransaction
        {
            public HidCollectionInfo Collection => collection;

            public ValueTask<IHidReportTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
                ValueTask.FromResult<IHidReportTransaction>(this);

            public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default)
            {
                owner.Writes.Enqueue(report.ToArray());
                return ValueTask.CompletedTask;
            }

            public async ValueTask<int> ReadAsync(Memory<byte> report, CancellationToken cancellationToken = default)
            {
                var frame = await reports.Reader.ReadAsync(cancellationToken);
                frame.CopyTo(report);
                return frame.Length;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

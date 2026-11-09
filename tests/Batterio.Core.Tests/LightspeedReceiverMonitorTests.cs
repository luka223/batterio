using System.Collections.Concurrent;
using System.Text.Json;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

/// <summary>The Lightspeed receiver announces the Superstrike going to sleep and waking up; the monitor turns that into the state of its route.</summary>
public sealed class LightspeedReceiverMonitorTests
{
    private const string SuperstrikeId = "logitech-g-pro-x2-superstrike";
    private const string ShortPath = @"\\?\hid#vid_046d&pid_c54d&mi_02&col01#8&39526b8b&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string LongPath = @"\\?\hid#vid_046d&pid_c54d&mi_02&col02#8&39526b8b&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";

    // Frames recorded on a real receiver (2026-11-09): the mouse fell asleep, then was moved.
    private static readonly byte[] LinkLost = Convert.FromHexString("1001411162BD40");
    private static readonly byte[] LinkEstablished = Convert.FromHexString("10014111A2BD40");

    [Fact]
    public void OnlyTheSuperstrikesConnectionNoticesAreDecoded()
    {
        Assert.True(LightspeedReceiverMonitor.TryDecodeNotice(LinkEstablished, 0x40BD, out var established));
        Assert.True(established);
        Assert.True(LightspeedReceiverMonitor.TryDecodeNotice(LinkLost, 0x40BD, out established));
        Assert.False(established);

        Assert.False(LightspeedReceiverMonitor.TryDecodeNotice(LinkLost, 0x1234, out _)); // another device on the same receiver
        Assert.False(LightspeedReceiverMonitor.TryDecodeNotice(Convert.FromHexString("10014011A2BD40"), 0x40BD, out _)); // not a 0x41 notice
        Assert.False(LightspeedReceiverMonitor.TryDecodeNotice(Convert.FromHexString("11014111A2BD40"), 0x40BD, out _)); // a long report
        Assert.False(LightspeedReceiverMonitor.TryDecodeNotice(Convert.FromHexString("10014111A2BD"), 0x40BD, out _)); // too short
    }

    [Fact]
    public async Task TheRouteFollowsTheMouseSleepingAndWaking()
    {
        var hid = new SteelSeriesHubMonitorTests.EventProvider();
        await using var monitor = new LightspeedReceiverMonitor(hid);
        var changes = new ConcurrentQueue<RouteState?>();
        monitor.StateChanged += (_, _) => changes.Enqueue(monitor.GetState(Receiver()));
        await monitor.SyncAsync([ShortCollection(), Receiver()]);
        await Eventually(() => hid.Opens == 1);
        Assert.Equal(RouteState.Unknown, monitor.GetState(Receiver()));

        hid.Push(Convert.FromHexString("11010400010101")); // the mouse's own reports are not notices
        hid.Push(LinkLost);
        await Eventually(() => changes.Count == 1);
        hid.Push(LinkLost); // a repeat changes nothing
        hid.Push(LinkEstablished);
        await Eventually(() => changes.Count == 2);

        Assert.Equal(new RouteState?[] { RouteState.Offline, RouteState.Online }, changes.ToArray());
        Assert.Empty(hid.Writes);
    }

    [Fact]
    public async Task OnlyTheLongCollectionOfThePluggedInReceiverHasAStateAndTheShortOneIsListenedTo()
    {
        var hid = new SteelSeriesHubMonitorTests.EventProvider();
        await using var monitor = new LightspeedReceiverMonitor(hid);
        Assert.Same(LightspeedReceiverMonitor.NoticeQuery, Assert.Single(monitor.Queries));
        Assert.Null(monitor.GetState(ShortCollection()));
        Assert.Null(monitor.GetState(Receiver() with { ProductId = 0x1234 }));

        await monitor.SyncAsync([Receiver()]); // no short collection: nothing to listen to
        Assert.Equal(0, hid.Opens);
        await monitor.SyncAsync([ShortCollection(), Receiver()]);
        await Eventually(() => hid.Opens == 1);
        hid.Push(LinkEstablished);
        await Eventually(() => monitor.GetState(Receiver()) == RouteState.Online);

        await monitor.SyncAsync([]); // receiver unplugged
        Assert.Equal(RouteState.Unknown, monitor.GetState(Receiver()));
    }

    [Fact]
    public async Task WakingTheMouseReadsItThroughDiscoveryWithoutAnyPolling()
    {
        var hid = new SteelSeriesHubMonitorTests.EventProvider { Collections = [ShortCollection(), Receiver()] };
        await using var monitor = new LightspeedReceiverMonitor(hid);
        var routes = new DeviceRouteTable();
        await using var discovery = new DeviceDiscoveryService(
            [BuiltInDevices.CatalogById[SuperstrikeId]], new AdapterRegistry([new NoopFactory()]), hid, null, routes, null, null, null,
            new DeviceDiscoveryOptions { FallbackInterval = TimeSpan.FromHours(1) }, [monitor]);
        var cameOnline = new ConcurrentQueue<string>();
        discovery.DeviceCameOnline += (_, e) => cameOnline.Enqueue(e.DeviceId);

        await discovery.DiscoverAsync();
        await discovery.StartAsync();
        var route = Assert.Single(routes.GetRoutes(SuperstrikeId));
        Assert.Equal(DeviceRouteKind.UsbReceiver, route.Kind);
        Assert.Equal(LongPath, route.EndpointPath); // the short collection never became a route
        await Eventually(() => hid.Opens == 1);

        hid.Push(LinkLost);
        await Eventually(() => routes.GetRoutes(SuperstrikeId).Single().State == RouteState.Offline);
        Assert.Empty(cameOnline);

        hid.Push(LinkEstablished);
        await Eventually(() => routes.GetRoutes(SuperstrikeId).Single().State == RouteState.Online);
        await Eventually(() => cameOnline.Count == 1);
    }

    private static HidCollectionInfo Receiver() => new()
    {
        DevicePath = LongPath,
        VendorId = 0x046D,
        ProductId = 0xC54D,
        InterfaceNumber = 2,
        UsagePage = 0xFF00,
        Usage = 0x0002,
        InputReportLength = 20,
        OutputReportLength = 20,
        SupportedReportIds = new HashSet<byte> { 0x11 }
    };

    private static HidCollectionInfo ShortCollection() => new()
    {
        DevicePath = ShortPath,
        VendorId = 0x046D,
        ProductId = 0xC54D,
        InterfaceNumber = 2,
        UsagePage = 0xFF00,
        Usage = 0x0001,
        InputReportLength = 7,
        OutputReportLength = 7,
        SupportedReportIds = new HashSet<byte> { 0x10 }
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
        public string AdapterId => SuperstrikeId;

        public void ValidateSettings(JsonElement settings) { }

        public IBatteryAdapter CreateAdapter(DeviceConfiguration device) => throw new NotSupportedException();
    }
}

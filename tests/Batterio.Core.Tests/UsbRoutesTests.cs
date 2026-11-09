using System.Text.Json;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

/// <summary>USB device routes: how discovery resolves them, and how a change of routes or a device coming online triggers an immediate read.</summary>
public sealed class UsbRoutesTests
{
    private const string SuperstrikeId = "logitech-g-pro-x2-superstrike";
    private const string KeychronId = "keychron-q1-he";

    private static readonly DeviceDiscoveryOptions SlowFallback = new()
    {
        FallbackInterval = TimeSpan.FromHours(1),
        EventDebounce = TimeSpan.FromMilliseconds(20)
    };

    // ----- resolution -----

    [Fact]
    public async Task UsbDevicesPublishRoutesAndAnAbsentUsbDeviceHasNone()
    {
        var hid = new ListProvider { Collections = [ReceiverCollection("receiver-b"), ReceiverCollection("receiver-a")] };
        var routes = new DeviceRouteTable();
        await using var discovery = new DeviceDiscoveryService(
            Catalog(SuperstrikeId, KeychronId), Registry(SuperstrikeId, KeychronId), hid, null, routes);

        var snapshot = await discovery.DiscoverAsync();

        var superstrike = snapshot.Devices.Single(device => device.DeviceId == SuperstrikeId);
        Assert.True(superstrike.IsPresent);
        Assert.False(superstrike.IsOnline);
        Assert.False(snapshot.Devices.Single(device => device.DeviceId == KeychronId).IsPresent);

        var published = routes.GetRoutes(SuperstrikeId);
        Assert.Equal(new[] { "receiver-a", "receiver-b" }, published.Select(route => route.EndpointPath));
        Assert.All(published, route =>
        {
            Assert.Equal(DeviceRouteKind.UsbReceiver, route.Kind);
            Assert.Equal(RouteState.Unknown, route.State);
            Assert.NotNull(route.Collection);
        });
        Assert.Empty(routes.GetRoutes(KeychronId));
    }

    [Fact]
    public async Task ACableRouteComesAfterTheReceiverAndDoesNotCountAsOnlineByItself()
    {
        var hid = new ListProvider { Collections = [ReceiverCollection("receiver")] };
        var routes = new DeviceRouteTable();
        await using var discovery = new DeviceDiscoveryService(Catalog(SuperstrikeId), Registry(SuperstrikeId), hid, null, routes);
        var cameOnline = new List<string>();
        discovery.DeviceCameOnline += (_, e) => cameOnline.Add(e.DeviceId);
        await discovery.DiscoverAsync();

        hid.Collections = [CableCollection("cable"), ReceiverCollection("receiver")];
        var snapshot = await discovery.DiscoverAsync();

        Assert.Empty(cameOnline); // plugging the cable in is announced by the change of routes, not as a device coming online
        Assert.False(snapshot.Devices.Single().IsOnline);
        var published = routes.GetRoutes(SuperstrikeId);
        Assert.Equal(new[] { DeviceRouteKind.UsbReceiver, DeviceRouteKind.UsbCable }, published.Select(route => route.Kind));
        Assert.All(published, route => Assert.Equal(RouteState.Unknown, route.State));

        // Unplugging the cable removes its route again.
        hid.Collections = [ReceiverCollection("receiver")];
        await discovery.DiscoverAsync();
        Assert.Equal(DeviceRouteKind.UsbReceiver, Assert.Single(routes.GetRoutes(SuperstrikeId)).Kind);
    }

    [Fact]
    public void TheResolverTakesTheStateOfAReceiverRouteFromItsSource()
    {
        var definition = BuiltInDevices.CatalogById[KeychronId];
        var collection = LinkCollection("link");
        var source = new FakeStateSource(RouteState.Offline);
        var resolver = new DeviceRouteResolver(usbSources: [source]);

        var (routes, failure) = resolver.Resolve(definition, [collection], new Dictionary<HidCollectionQuery, Exception>());

        Assert.Null(failure);
        Assert.Equal(RouteState.Offline, Assert.Single(routes).State);
        source.State = null;
        Assert.Equal(RouteState.Unknown, Assert.Single(resolver.Resolve(definition, [collection], new Dictionary<HidCollectionQuery, Exception>()).Routes).State);
    }

    [Fact]
    public void TheResolverReturnsTheFailureOfAQueryThatFoundNothing()
    {
        var definition = BuiltInDevices.CatalogById[SuperstrikeId];
        var failed = new Dictionary<HidCollectionQuery, Exception> { [LogitechGProX2Superstrike.ReceiverQuery] = new InvalidOperationException("boom") };
        var resolver = new DeviceRouteResolver();

        var (none, failure) = resolver.Resolve(definition, [], failed);
        Assert.Empty(none);
        Assert.Equal("boom", failure?.Message);

        // Discovery looks at the failure only when no route was found at all, so a device on its cable stays present.
        var (some, _) = resolver.Resolve(definition, [CableCollection("cable")], failed);
        Assert.Single(some);
    }

    // ----- immediate reads -----

    [Fact]
    public async Task APlugOrUnplugOfTheCableReadsTheDeviceOnceEach()
    {
        await using var rig = await Rig.StartAsync([ReceiverCollection("receiver")]);
        await rig.WaitForReads(1);

        // The change of routes announces the plug-in; it costs one read.
        rig.Hid.Collections = [ReceiverCollection("receiver"), CableCollection("cable")];
        await rig.Selection.RefreshDiscoveryAsync();
        await rig.WaitForReads(2);
        await rig.AssertNoFurtherReads(2);

        rig.Hid.Collections = [ReceiverCollection("receiver")];
        await rig.Selection.RefreshDiscoveryAsync();
        await rig.WaitForReads(3);
        await rig.AssertNoFurtherReads(3);
    }

    [Fact]
    public async Task ABurstOfRouteChangesWithinTheWindowIsOneRefresh()
    {
        await using var rig = await Rig.StartAsync([ReceiverCollection("receiver")], TimeSpan.FromMilliseconds(250));
        await rig.WaitForReads(1);

        foreach (var name in new[] { "cable-a", "cable-b", "cable-c" })
        {
            rig.Routes.SetRoutes(SuperstrikeId, [rig.ReceiverRoute(), CableRoute(name)]);
        }

        await rig.WaitForReads(2);
        await rig.AssertNoFurtherReads(2);
    }

    [Fact]
    public async Task ARouteThatOnlyChangesItsStateIsNotReadBecauseThatMightWakeASleepingDevice()
    {
        await using var rig = await Rig.StartAsync([ReceiverCollection("receiver")]);
        await rig.WaitForReads(1);

        rig.Routes.SetRoutes(SuperstrikeId, [rig.ReceiverRoute() with { State = RouteState.Offline }]);

        await rig.AssertNoFurtherReads(1);
    }

    [Fact]
    public async Task ADeviceThatJustAppearedIsReadOnlyByItsNewPolling()
    {
        await using var rig = await Rig.StartAsync([]);
        await rig.AssertNoFurtherReads(0);

        rig.Hid.Collections = [ReceiverCollection("receiver")];
        await rig.Selection.RefreshDiscoveryAsync();

        await rig.WaitForReads(1);
        await rig.AssertNoFurtherReads(1);
    }

    [Fact]
    public async Task NoRefreshIsQueuedWhileTheDeviceIsBeingReadAndAtMostOneFollowsIt()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = await Rig.StartAsync(
            [ReceiverCollection("receiver")], TimeSpan.FromMilliseconds(50), read => read >= 2 ? release.Task : Task.CompletedTask);
        await rig.WaitForReads(1);

        rig.Routes.SetRoutes(SuperstrikeId, [rig.ReceiverRoute(), CableRoute("cable-a")]);
        await rig.WaitForReads(2); // this read is now stuck

        rig.Routes.SetRoutes(SuperstrikeId, [rig.ReceiverRoute(), CableRoute("cable-b")]);
        rig.Routes.SetRoutes(SuperstrikeId, [rig.ReceiverRoute(), CableRoute("cable-c")]);
        await rig.AssertNoFurtherReads(2);

        release.SetResult();
        await rig.WaitForReads(3);
        await rig.AssertNoFurtherReads(3);
    }

    // ----- helpers -----

    private static BuiltInDeviceDefinition[] Catalog(params string[] ids) => ids.Select(id => BuiltInDevices.CatalogById[id]).ToArray();

    private static AdapterRegistry Registry(params string[] ids) =>
        new(ids.Select(id => (IBatteryAdapterFactory)new CountingFactory(id, _ => Task.CompletedTask, new Counter())));

    private static ResolvedRoute CableRoute(string path) =>
        new(SuperstrikeId, DeviceRouteKind.UsbCable, path, null, RouteState.Online, CableCollection(path));

    private static HidCollectionInfo ReceiverCollection(string path) => CollectionFor(LogitechGProX2Superstrike.ReceiverQuery, path);

    private static HidCollectionInfo CableCollection(string path) => CollectionFor(LogitechGProX2Superstrike.WiredQuery, path);

    private static HidCollectionInfo LinkCollection(string path) => CollectionFor(KeychronQ1He.LinkRawHidQuery, path);

    internal static HidCollectionInfo CollectionFor(HidCollectionQuery query, string path) => new()
    {
        DevicePath = path,
        VendorId = query.VendorId,
        ProductId = query.ProductId,
        InterfaceNumber = query.InterfaceNumber,
        UsagePage = query.UsagePage,
        Usage = query.Usage,
        InputReportLength = Math.Max(query.MinimumReportLength, 20),
        OutputReportLength = Math.Max(query.MinimumReportLength, 20),
        SupportedReportIds = new HashSet<byte> { query.RequiredReportId ?? 0 }
    };

    private sealed class Counter
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public int Next() => Interlocked.Increment(ref _reads);
    }

    private sealed class CountingFactory(string adapterId, Func<int, Task> onRead, Counter counter) : IBatteryAdapterFactory
    {
        public string AdapterId { get; } = adapterId;

        public void ValidateSettings(JsonElement settings) { }

        public IBatteryAdapter CreateAdapter(DeviceConfiguration device) => new CountingAdapter(onRead, counter);
    }

    private sealed class CountingAdapter(Func<int, Task> onRead, Counter counter) : IBatteryAdapter
    {
        public async Task<BatteryReading> GetBatteryStatus(CancellationToken cancellationToken = default)
        {
            await onRead(counter.Next()).WaitAsync(cancellationToken);
            return new BatteryReading(50, BatteryChargeState.NotCharging);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ListProvider : IHidCollectionProvider
    {
        public IReadOnlyList<HidCollectionInfo> Collections { get; set; } = [];

        public IReadOnlyList<HidCollectionInfo> Enumerate(HidCollectionQuery query) => Collections;

        public ValueTask<IHidReportTransport> OpenAsync(HidCollectionInfo collection, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IHidReportTransport>(new InvalidOperationException("The tests never open HID transports."));
    }

    /// <summary>A source that reports <paramref name="state"/> for the collections <paramref name="appliesTo"/> accepts (all if null).</summary>
    internal sealed class FakeStateSource(RouteState? state, Func<HidCollectionInfo, bool>? appliesTo = null) : IUsbRouteStateSource
    {
        public RouteState? State { get; set; } = state;

        public IReadOnlyCollection<HidCollectionQuery> Queries => [];

        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public RouteState? GetState(HidCollectionInfo collection) => appliesTo is null || appliesTo(collection) ? State : null;

        public Task SyncAsync(IReadOnlyList<HidCollectionInfo> collections, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>Discovery, polling and selection wired as in the app, with a counting adapter for the Superstrike.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly TempDirectory _directory = new();
        private readonly Counter _counter;
        private readonly PollingCoordinator _polling;
        private readonly DeviceDiscoveryService _discovery;
        private readonly JsonUserSettingsStore _store;

        private Rig(ListProvider hid, DeviceRouteTable routes, Counter counter, Func<int, Task> onRead, TimeSpan window)
        {
            Hid = hid;
            Routes = routes;
            _counter = counter;
            var definition = BuiltInDevices.CatalogById[SuperstrikeId];
            var registry = new AdapterRegistry([new CountingFactory(SuperstrikeId, onRead, counter)]);
            _polling = new PollingCoordinator(registry);
            _discovery = new DeviceDiscoveryService([definition], registry, hid, null, routes, null, null, null, SlowFallback);
            _store = new JsonUserSettingsStore(Path.Combine(_directory.Path, "settings.json"));
            Selection = new DeviceSelectionCoordinator([definition], _store, _discovery, _polling, null, routes, window);
        }

        public ListProvider Hid { get; }

        public DeviceRouteTable Routes { get; }

        public DeviceSelectionCoordinator Selection { get; }

        public static async Task<Rig> StartAsync(
            IEnumerable<HidCollectionInfo> collections,
            TimeSpan? window = null,
            Func<int, Task>? onRead = null)
        {
            var rig = new Rig(
                new ListProvider { Collections = collections.ToArray() }, new DeviceRouteTable(), new Counter(),
                onRead ?? (_ => Task.CompletedTask), window ?? TimeSpan.FromMilliseconds(50));
            await rig.Selection.StartAsync();
            return rig;
        }

        public ResolvedRoute ReceiverRoute() => Routes.GetRoutes(SuperstrikeId).Single(route => route.Kind == DeviceRouteKind.UsbReceiver);

        public async Task WaitForReads(int count)
        {
            for (var attempt = 0; attempt < 150 && _counter.Reads < count; attempt++)
            {
                await Task.Delay(20);
            }

            Assert.Equal(count, _counter.Reads);
        }

        /// <summary>Waits long enough for a coalesced refresh to have happened, then checks that none did.</summary>
        public async Task AssertNoFurtherReads(int count)
        {
            await Task.Delay(600);
            Assert.Equal(count, _counter.Reads);
        }

        public async ValueTask DisposeAsync()
        {
            await Selection.DisposeAsync();
            await _polling.DisposeAsync();
            await _discovery.DisposeAsync();
            await _store.DisposeAsync();
            _directory.Dispose();
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Batterio.Core.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}

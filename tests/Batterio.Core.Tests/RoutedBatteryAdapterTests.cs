using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

public sealed class RoutedBatteryAdapterTests
{
    private const string DeviceId = "device";

    [Fact]
    public async Task OnlineRoutesAreTriedFirstThenUnknownAndOfflineOnesAreSkipped()
    {
        var table = Table(
            Route(DeviceRouteKind.LogiBolt, "bolt", RouteState.Offline, slot: 2),
            Route(DeviceRouteKind.HidppBluetooth, "bt-unknown", RouteState.Unknown),
            Route(DeviceRouteKind.HidppBluetooth, "bt-online", RouteState.Online));
        var routes = new ScriptedRoutes();
        routes.Fail("bt-online", Offline());
        routes.Succeed("bt-unknown", 33);
        await using var adapter = Adapter(table, routes);

        var reading = await adapter.GetBatteryStatus();

        Assert.Equal(33, reading.Percentage);
        Assert.Equal(new[] { "bt-online", "bt-unknown" }, routes.Calls); // the offline Bolt route was never asked
    }

    [Fact]
    public async Task OfflineRoutesAreTriedWhenNothingElseIsLeft()
    {
        var table = Table(Route(DeviceRouteKind.LogiBolt, "bolt", RouteState.Offline, slot: 2));
        var routes = new ScriptedRoutes();
        routes.Succeed("bolt", 51); // the reported state was stale
        await using var adapter = Adapter(table, routes);

        Assert.Equal(51, (await adapter.GetBatteryStatus()).Percentage);
    }

    [Fact]
    public async Task TheRouteThatWorkedLastTimeIsTriedFirst()
    {
        var table = Table(
            Route(DeviceRouteKind.HidppBluetooth, "first", RouteState.Unknown),
            Route(DeviceRouteKind.LogiBolt, "second", RouteState.Unknown, slot: 1));
        var routes = new ScriptedRoutes();
        routes.Fail("first", Offline());
        routes.Succeed("second", 44);
        await using var adapter = Adapter(table, routes);

        await adapter.GetBatteryStatus();
        Assert.Equal(new[] { "first", "second" }, routes.Calls);

        routes.ClearCalls();
        await adapter.GetBatteryStatus();
        Assert.Equal(new[] { "second" }, routes.Calls);

        // When the remembered route stops working, the others are tried again.
        routes.Fail("second", Offline());
        routes.Succeed("first", 45);
        routes.ClearCalls();
        Assert.Equal(45, (await adapter.GetBatteryStatus()).Percentage);
        Assert.Equal(new[] { "second", "first" }, routes.Calls);
    }

    [Fact]
    public async Task OnlineStillBeatsTheRememberedRoute()
    {
        var table = Table(
            Route(DeviceRouteKind.HidppBluetooth, "bt", RouteState.Unknown),
            Route(DeviceRouteKind.LogiBolt, "bolt", RouteState.Unknown, slot: 1));
        var routes = new ScriptedRoutes();
        routes.Succeed("bt", 10);
        routes.Succeed("bolt", 20);
        await using var adapter = Adapter(table, routes);
        await adapter.GetBatteryStatus(); // bt works and is remembered

        table.SetRoutes(DeviceId, new[]
        {
            Route(DeviceRouteKind.HidppBluetooth, "bt", RouteState.Unknown),
            Route(DeviceRouteKind.LogiBolt, "bolt", RouteState.Online, slot: 1)
        });
        routes.ClearCalls();

        Assert.Equal(20, (await adapter.GetBatteryStatus()).Percentage);
        Assert.Equal(new[] { "bolt" }, routes.Calls);
    }

    [Fact]
    public async Task TheErrorIsOfflineOnlyWhenEveryRouteWasOffline()
    {
        var table = Table(
            Route(DeviceRouteKind.HidppBluetooth, "bt", RouteState.Unknown),
            Route(DeviceRouteKind.LogiBolt, "bolt", RouteState.Unknown, slot: 1));
        var routes = new ScriptedRoutes();
        routes.Fail("bt", Offline());
        routes.Fail("bolt", Offline());
        await using var adapter = Adapter(table, routes);

        var allOffline = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());
        Assert.Equal(BatteryAdapterErrorKind.DeviceOffline, allOffline.Kind);

        routes.Fail("bolt", new BatteryAdapterException(BatteryAdapterErrorKind.AccessFailure, "receiver is closed"));
        var mixed = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());
        Assert.Equal(BatteryAdapterErrorKind.AccessFailure, mixed.Kind);
        Assert.Contains("closed", mixed.Message);
    }

    [Fact]
    public async Task WithoutRoutesTheReceiverIsReportedMissing()
    {
        await using var adapter = Adapter(new DeviceRouteTable(), new ScriptedRoutes());

        var exception = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());
        Assert.Equal(BatteryAdapterErrorKind.ReceiverMissing, exception.Kind);
    }

    [Fact]
    public async Task TheDevicePathSettingRestrictsTheRoutes()
    {
        var table = Table(
            Route(DeviceRouteKind.HidppBluetooth, "bt-a", RouteState.Online),
            Route(DeviceRouteKind.HidppBluetooth, "bt-b", RouteState.Online));
        var routes = new ScriptedRoutes();
        routes.Succeed("bt-a", 12);
        routes.Succeed("bt-b", 71);
        await using var adapter = Adapter(table, routes, devicePath: "BT-B");

        Assert.Equal(71, (await adapter.GetBatteryStatus()).Percentage);
        Assert.Equal(new[] { "bt-b" }, routes.Calls);

        await using var missing = Adapter(table, routes, devicePath: "bt-c");
        var exception = await Assert.ThrowsAsync<BatteryAdapterException>(() => missing.GetBatteryStatus());
        Assert.Equal(BatteryAdapterErrorKind.ReceiverMissing, exception.Kind);
    }

    [Fact]
    public async Task RoutesPublishedLaterAreUsedWithoutRecreatingTheAdapter()
    {
        var table = new DeviceRouteTable();
        var routes = new ScriptedRoutes();
        routes.Succeed("bt", 61);
        await using var adapter = Adapter(table, routes);

        await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());
        table.SetRoutes(DeviceId, new[] { Route(DeviceRouteKind.HidppBluetooth, "bt", RouteState.Online) });

        Assert.Equal(61, (await adapter.GetBatteryStatus()).Percentage);
    }

    [Fact]
    public async Task EachPollLogsTheRouteAndTheElapsedTime()
    {
        var log = new ListLog();
        var table = Table(Route(DeviceRouteKind.HidppBluetooth, "bt", RouteState.Online));
        var routes = new ScriptedRoutes();
        routes.Succeed("bt", 61);
        await using var adapter = new RoutedBatteryAdapter(DeviceId, "Test device", routes.Map(), table, log);

        await adapter.GetBatteryStatus();

        Assert.Contains(log.Messages, message =>
            message.Contains("Test device query via HidppBluetooth route 'bt' finished in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelledAndDisposedAdaptersThrow()
    {
        var table = Table(Route(DeviceRouteKind.HidppBluetooth, "bt", RouteState.Online));
        var routes = new ScriptedRoutes();
        routes.Succeed("bt", 61);
        var adapter = Adapter(table, routes);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetBatteryStatus(cancellation.Token));
        await adapter.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => adapter.GetBatteryStatus());
    }

    [Fact]
    public void RouteTableAnnouncesOnlyRealChanges()
    {
        var table = new DeviceRouteTable();
        var announced = new List<string>();
        table.RoutesChanged += (_, e) => announced.Add(e.DeviceId);
        var online = Route(DeviceRouteKind.LogiBolt, "bolt", RouteState.Online, slot: 3);

        Assert.True(table.SetRoutes(DeviceId, new[] { online }));
        Assert.False(table.SetRoutes(DeviceId, new[] { online with { Collection = new HidCollectionInfo { DevicePath = "x", VendorId = 1, ProductId = 2 } } }));
        Assert.True(table.SetRoutes(DeviceId, new[] { online with { State = RouteState.Offline } }));
        Assert.True(table.SetRoutes(DeviceId, Array.Empty<ResolvedRoute>()));
        Assert.False(table.SetRoutes(DeviceId, Array.Empty<ResolvedRoute>()));

        Assert.Equal(new[] { DeviceId, DeviceId, DeviceId }, announced);
        Assert.Empty(table.GetRoutes("DEVICE"));
    }

    private static RoutedBatteryAdapter Adapter(DeviceRouteTable table, ScriptedRoutes routes, string? devicePath = null) =>
        new(DeviceId, "Test device", routes.Map(), table, new NullBatterioLog(), devicePath);

    private static DeviceRouteTable Table(params ResolvedRoute[] routes)
    {
        var table = new DeviceRouteTable();
        table.SetRoutes(DeviceId, routes);
        return table;
    }

    private static ResolvedRoute Route(DeviceRouteKind kind, string path, RouteState state, byte? slot = null) =>
        new(DeviceId, kind, path, slot, state);

    private static BatteryAdapterException Offline() =>
        new(BatteryAdapterErrorKind.DeviceOffline, "The device did not answer.");

    /// <summary>One scripted route implementation for each kind; the outcome depends on the endpoint path of the route.</summary>
    private sealed class ScriptedRoutes
    {
        private readonly Dictionary<string, Func<BatteryReading>> _outcomes = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _calls = new();

        public string[] Calls
        {
            get
            {
                lock (_calls) return _calls.ToArray();
            }
        }

        public void ClearCalls()
        {
            lock (_calls) _calls.Clear();
        }

        public void Succeed(string path, int percentage) =>
            _outcomes[path] = () => new BatteryReading(percentage, BatteryChargeState.NotCharging);

        public void Fail(string path, BatteryAdapterException exception) =>
            _outcomes[path] = () => throw exception;

        public IReadOnlyDictionary<DeviceRouteKind, IBatteryRoute> Map() => new Dictionary<DeviceRouteKind, IBatteryRoute>
        {
            [DeviceRouteKind.HidppBluetooth] = new Route(this),
            [DeviceRouteKind.LogiBolt] = new Route(this)
        };

        private sealed class Route(ScriptedRoutes owner) : IBatteryRoute
        {
            public Task<BatteryReading> ReadAsync(ResolvedRoute route, CancellationToken cancellationToken)
            {
                lock (owner._calls) owner._calls.Add(route.EndpointPath);
                try
                {
                    return Task.FromResult(owner._outcomes[route.EndpointPath]());
                }
                catch (BatteryAdapterException exception)
                {
                    return Task.FromException<BatteryReading>(exception);
                }
            }
        }
    }
}

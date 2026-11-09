using System.Collections.Concurrent;
using System.Text.Json;
using Batterio.Core;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

public sealed class SettingsDiscoveryTests
{
    [Fact]
    public async Task MissingSettingsDefaultToSelectedAndSelectionsSurviveReconnect()
    {
        using var fixture = new TempDirectory();
        var store = new JsonUserSettingsStore(Path.Combine(fixture.Path, "settings.json"));

        var defaults = await store.LoadAsync();
        Assert.True(defaults.IsDeviceSelected("Device-A"));

        await store.SaveAsync(defaults.WithDeviceSelection("Device-A", false));
        var reloaded = await store.LoadAsync();
        Assert.False(reloaded.IsDeviceSelected("device-a"));
        Assert.True(reloaded.IsDeviceSelected("unseen-device"));
        Assert.True(File.Exists(store.BackupFilePath) == false);

        await store.SaveAsync(reloaded.WithDeviceSelection("Device-B", true));
        Assert.True(File.Exists(store.BackupFilePath));
        Assert.False((await store.LoadAsync()).IsDeviceSelected("DEVICE-A"));
    }

    [Fact]
    public async Task CorruptAndUnsupportedSettingsUseDefaultsWithoutReplacingTheFile()
    {
        using var fixture = new TempDirectory();
        var path = Path.Combine(fixture.Path, "settings.json");
        var log = new RecordingLog();
        await File.WriteAllTextAsync(path, "{\"version\":99,\"deviceSelections\":{\"mouse\":false}}");
        var original = await File.ReadAllTextAsync(path);
        var store = new JsonUserSettingsStore(path, log);

        var settings = await store.LoadAsync();

        Assert.True(settings.IsDeviceSelected("mouse"));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.NotEmpty(log.Messages);
    }

    [Fact]
    public async Task FailedSaveLeavesDestinationUntouched()
    {
        using var fixture = new TempDirectory();
        var path = Path.Combine(fixture.Path, "settings.json");
        Directory.CreateDirectory(path);
        await using var store = new JsonUserSettingsStore(path);

        await Assert.ThrowsAsync<UserSettingsStoreException>(() =>
            store.SaveAsync(UserSettings.Defaults().WithDeviceSelection("mouse", false)));

        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task DiscoveryFiltersUnregisteredAdaptersAndDeduplicatesCollectionsWithoutOpeningThem()
    {
        var first = Definition("first", 0x1000);
        var second = Definition("second", 0x1001);
        var provider = new FakeHidProvider
        {
            Collections = new[] { Collection(FirstQuery(first)), Collection(FirstQuery(first)) }
        };
        var registry = new AdapterRegistry(new[] { new TestFactory(first.AdapterId) });
        await using var discovery = new DeviceDiscoveryService(new[] { first, second }, registry, provider);

        var snapshot = await discovery.DiscoverAsync();

        var state = Assert.Single(snapshot.Devices);
        Assert.Equal(first.Id, state.DeviceId);
        Assert.True(state.IsPresent);
        Assert.Equal(1, provider.Enumerations);
        Assert.Equal(0, provider.Opens);
    }

    [Fact]
    public async Task DiscoveryFailureRetainsLastPresenceAndReportsTheError()
    {
        var definition = Definition("first", 0x1000);
        var provider = new FakeHidProvider { Collections = new[] { Collection(FirstQuery(definition)) } };
        var registry = new AdapterRegistry(new[] { new TestFactory(definition.AdapterId) });
        await using var discovery = new DeviceDiscoveryService(new[] { definition }, registry, provider);

        await discovery.DiscoverAsync();
        provider.ThrowOnEnumerate = true;
        var failed = await discovery.DiscoverAsync();

        var state = Assert.Single(failed.Devices);
        Assert.True(state.IsPresent);
        Assert.Contains("enumeration", state.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoveryIsPresentWhenAnyPresenceQueryMatchesAndEnumeratesOncePerScan()
    {
        var wiredQuery = Query(0xC0A8);
        var receiverQuery = Query(0xC54D);
        var definition = Definition("superstrike", wiredQuery, receiverQuery);
        var registry = new AdapterRegistry(new[] { new TestFactory(definition.AdapterId) });
        var wiredProvider = new FakeHidProvider { Collections = new[] { Collection(wiredQuery) } };
        await using (var discovery = new DeviceDiscoveryService(new[] { definition }, registry, wiredProvider))
        {
            var snapshot = await discovery.DiscoverAsync();
            Assert.True(Assert.Single(snapshot.Devices).IsPresent);
            Assert.False(snapshot.Devices[0].IsOnline); // presence-only devices have no online state
            Assert.Equal(0, wiredProvider.Opens);
        }

        var receiverProvider = new FakeHidProvider { Collections = new[] { Collection(receiverQuery) } };
        await using var receiverDiscovery = new DeviceDiscoveryService(new[] { definition }, registry, receiverProvider);
        var receiverSnapshot = await receiverDiscovery.DiscoverAsync();
        Assert.True(Assert.Single(receiverSnapshot.Devices).IsPresent);
        // Both queries are served by one pass (the default combined enumeration runs each distinct query once).
        Assert.Equal(new ushort[] { 0xC0A8, 0xC54D }, receiverProvider.EnumeratedProductIds);
        Assert.Equal(0, receiverProvider.Opens);
    }

    [Fact]
    public async Task MultiQueryFailureRetainsPresenceWhenNoQueryCanProveIt()
    {
        var wiredQuery = Query(0xC0A8);
        var receiverQuery = Query(0xC54D);
        var definition = Definition("superstrike", wiredQuery, receiverQuery);
        var provider = new FakeHidProvider { Collections = new[] { Collection(receiverQuery) } };
        var registry = new AdapterRegistry(new[] { new TestFactory(definition.AdapterId) });
        await using var discovery = new DeviceDiscoveryService(new[] { definition }, registry, provider);

        await discovery.DiscoverAsync();
        provider.Collections = Array.Empty<HidCollectionInfo>();
        provider.ThrowForProductIds.Add(wiredQuery.ProductId);
        var failed = await discovery.DiscoverAsync();

        var state = Assert.Single(failed.Devices);
        Assert.True(state.IsPresent);
        Assert.Contains("enumeration", state.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelectionCoordinatorUsesThePresentAndSelectedIntersection()
    {
        using var fixture = new TempDirectory();
        var definition = Definition("first", 0x1000);
        var collection = Collection(FirstQuery(definition));
        var provider = new FakeHidProvider { Collections = new[] { collection } };
        var factory = new TestFactory(definition.AdapterId);
        var registry = new AdapterRegistry(new[] { factory });
        await using var polling = new PollingCoordinator(registry);
        await using var discovery = new DeviceDiscoveryService(new[] { definition }, registry, provider);
        await using var store = new JsonUserSettingsStore(Path.Combine(fixture.Path, "settings.json"));
        await using var selection = new DeviceSelectionCoordinator(
            new[] { definition }, store, discovery, polling);

        await selection.StartAsync();
        Assert.Single(polling.States);
        Assert.True(Assert.Single(selection.CurrentSnapshot.Devices).IsSelected);

        await selection.SetDeviceSelectedAsync(definition.Id, false);
        Assert.Empty(polling.States);

        provider.Collections = Array.Empty<HidCollectionInfo>();
        await selection.RefreshDiscoveryAsync();
        Assert.False(Assert.Single(selection.CurrentSnapshot.Devices).IsPresent);

        await selection.SetDeviceSelectedAsync(definition.Id, true);
        Assert.Empty(polling.States);
        provider.Collections = new[] { collection };
        await selection.RefreshDiscoveryAsync();
        await Eventually(() => polling.States.Count == 1);
    }

    [Fact]
    public async Task SelectionCoordinatorListsOnlyDevicesThatHaveBeenConnected()
    {
        using var fixture = new TempDirectory();
        var owned = Definition("owned", Query(0x1000));
        var neverOwned = Definition("never-owned", Query(0x2000));
        var ownedCollection = Collection(FirstQuery(owned));
        var provider = new FakeHidProvider { Collections = Array.Empty<HidCollectionInfo>() };
        var registry = new AdapterRegistry(new[] { new TestFactory(owned.AdapterId), new TestFactory(neverOwned.AdapterId) });
        var settingsPath = Path.Combine(fixture.Path, "settings.json");

        await using (var polling = new PollingCoordinator(registry))
        await using (var discovery = new DeviceDiscoveryService(new[] { owned, neverOwned }, registry, provider))
        await using (var store = new JsonUserSettingsStore(settingsPath))
        await using (var selection = new DeviceSelectionCoordinator(new[] { owned, neverOwned }, store, discovery, polling))
        {
            await selection.StartAsync();
            Assert.Empty(selection.CurrentSnapshot.Devices);

            provider.Collections = new[] { ownedCollection };
            await selection.RefreshDiscoveryAsync();
            var listed = Assert.Single(selection.CurrentSnapshot.Devices);
            Assert.Equal(owned.Id, listed.DeviceId);
            Assert.True(listed.IsPresent);

            provider.Collections = Array.Empty<HidCollectionInfo>();
            await selection.RefreshDiscoveryAsync();
            listed = Assert.Single(selection.CurrentSnapshot.Devices);
            Assert.Equal(owned.Id, listed.DeviceId);
            Assert.False(listed.IsPresent);
        }

        await using (var polling = new PollingCoordinator(registry))
        await using (var discovery = new DeviceDiscoveryService(new[] { owned, neverOwned }, registry, provider))
        await using (var store = new JsonUserSettingsStore(settingsPath))
        await using (var selection = new DeviceSelectionCoordinator(new[] { owned, neverOwned }, store, discovery, polling))
        {
            await selection.StartAsync();
            var listed = Assert.Single(selection.CurrentSnapshot.Devices);
            Assert.Equal(owned.Id, listed.DeviceId);
            Assert.False(listed.IsPresent);
        }
    }

    [Fact]
    public async Task StoredSelectionsDoNotMakeDevicesKnownAndAreResetOnFirstConnection()
    {
        using var fixture = new TempDirectory();
        var path = Path.Combine(fixture.Path, "settings.json");
        await File.WriteAllTextAsync(path, "{\"version\":1,\"deviceSelections\":{\"mouse\":false}}");
        var store = new JsonUserSettingsStore(path);

        var settings = await store.LoadAsync();
        Assert.False(settings.IsDeviceKnown("MOUSE"));
        Assert.False(settings.IsDeviceSelected("mouse"));

        await store.SaveAsync(settings.WithKnownDevices(new[] { "Mouse" }));
        var reloaded = await store.LoadAsync();
        Assert.True(reloaded.IsDeviceKnown("mouse"));
        Assert.True(reloaded.IsDeviceSelected("mouse"));

        var hidden = reloaded.WithDeviceSelection("mouse", false).WithKnownDevices(new[] { "mouse" });
        Assert.False(hidden.IsDeviceSelected("mouse"));
    }

    [Fact]
    public async Task TaskbarSelectionsDefaultToOffAndRoundTrip()
    {
        using var fixture = new TempDirectory();
        var store = new JsonUserSettingsStore(Path.Combine(fixture.Path, "settings.json"));

        var defaults = await store.LoadAsync();
        Assert.False(defaults.IsDeviceOnTaskbar("Device-A"));

        await store.SaveAsync(defaults.WithTaskbarSelection("Device-A", true));
        var json = await File.ReadAllTextAsync(store.FilePath);
        Assert.Contains("taskbarSelections", json);

        var reloaded = await store.LoadAsync();
        Assert.True(reloaded.IsDeviceOnTaskbar("device-a"));
        Assert.False(reloaded.IsDeviceOnTaskbar("device-b"));
        Assert.True(reloaded.IsDeviceSelected("device-a"));
    }

    [Fact]
    public async Task SettingsFilesWithoutTaskbarSelectionsLoadWithNothingOnTheTaskbar()
    {
        using var fixture = new TempDirectory();
        var path = Path.Combine(fixture.Path, "settings.json");
        await File.WriteAllTextAsync(path, "{\"version\":1,\"deviceSelections\":{\"mouse\":false},\"knownDevices\":[\"mouse\"]}");
        var store = new JsonUserSettingsStore(path);

        var settings = await store.LoadAsync();

        Assert.False(settings.IsDeviceOnTaskbar("mouse"));
        Assert.False(settings.IsDeviceSelected("mouse"));
        Assert.True(settings.IsDeviceKnown("mouse"));
    }

    [Fact]
    public void SettingsTransformsPreserveTaskbarSelections()
    {
        var settings = UserSettings.Defaults()
            .WithTaskbarSelection("mouse", true)
            .WithTaskbarSelection("keyboard", false);

        var selected = settings.WithDeviceSelection("mouse", false);
        Assert.True(selected.IsDeviceOnTaskbar("mouse"));
        Assert.False(selected.IsDeviceSelected("mouse"));

        var known = settings.WithKnownDevices(new[] { "other" });
        Assert.True(known.IsDeviceOnTaskbar("mouse"));
        Assert.True(known.IsDeviceKnown("other"));

        var normalized = new UserSettings
        {
            TaskbarSelections = new Dictionary<string, bool> { [" Mouse "] = true }
        }.Normalize();
        Assert.True(normalized.IsDeviceOnTaskbar("MOUSE"));

        var toggled = settings.WithTaskbarSelection("mouse", false);
        Assert.False(toggled.IsDeviceOnTaskbar("mouse"));
        Assert.True(settings.IsDeviceOnTaskbar("mouse"));
    }

    [Fact]
    public async Task SelectionCoordinatorPublishesTheTaskbarFlagWithoutChangingPolling()
    {
        using var fixture = new TempDirectory();
        var definition = Definition("first", 0x1000);
        var provider = new FakeHidProvider { Collections = new[] { Collection(FirstQuery(definition)) } };
        var registry = new AdapterRegistry(new[] { new TestFactory(definition.AdapterId) });
        var settingsPath = Path.Combine(fixture.Path, "settings.json");
        await using var polling = new PollingCoordinator(registry);
        await using var discovery = new DeviceDiscoveryService(new[] { definition }, registry, provider);
        await using var store = new JsonUserSettingsStore(settingsPath);
        await using var selection = new DeviceSelectionCoordinator(new[] { definition }, store, discovery, polling);

        await selection.StartAsync();
        Assert.False(Assert.Single(selection.CurrentSnapshot.Devices).IsOnTaskbar);
        Assert.Equal(DeviceKind.Mouse, Assert.Single(selection.CurrentSnapshot.Devices).Kind);

        var published = new List<DeviceSelectionSnapshot>();
        selection.SnapshotChanged += (_, e) => published.Add(e.Snapshot);

        await selection.SetDeviceOnTaskbarAsync(definition.Id, true);

        var state = Assert.Single(selection.CurrentSnapshot.Devices);
        Assert.True(state.IsOnTaskbar);
        Assert.True(state.IsSelected);
        Assert.Single(polling.States);
        Assert.Contains(published, snapshot => snapshot.Devices.Single().IsOnTaskbar);
        Assert.True((await store.LoadAsync()).IsDeviceOnTaskbar(definition.Id));

        await selection.SetDeviceSelectedAsync(definition.Id, false);
        state = Assert.Single(selection.CurrentSnapshot.Devices);
        Assert.False(state.IsSelected);
        Assert.True(state.IsOnTaskbar);
    }

    // --- routed devices (Bluetooth and Logi Bolt) ---

    private const string BoltPath = FakeHidpp.ReceiverLongPath;
    private const string MouseId = "logitech-mx-master-3s";
    private const string KeyboardId = "logitech-mx-keys-mechanical";
    private const string KeyboardBluetoothPath = "bt-b366";
    private const string MouseBluetoothPath = "bt-b034";

    private static DeviceDiscoveryOptions SlowFallback { get; } = new()
    {
        FallbackInterval = TimeSpan.FromMinutes(5),
        EventDebounce = TimeSpan.FromMilliseconds(80)
    };

    [Fact]
    public async Task MouseOnlyOnBoltIsPresentAndTheKeyboardIsNotListed()
    {
        var hid = new FakeHidpp().Receiver().Add(BoltPath, 2, FakeHidppDevice.MxMaster3s(percentage: 64));
        var routes = new DeviceRouteTable();
        await using var bolt = new LogiBoltReceiverManager(hid, null, LogiBoltReceiverTests.FastOptions);
        await using var discovery = new DeviceDiscoveryService(MxCatalog(), MxRegistry(), hid, null, routes, bolt);

        var snapshot = await discovery.DiscoverAsync();

        var mouse = snapshot.Devices.Single(device => device.DeviceId == MouseId);
        var keyboard = snapshot.Devices.Single(device => device.DeviceId == KeyboardId);
        Assert.True(mouse.IsPresent);
        Assert.True(mouse.IsOnline);
        Assert.False(keyboard.IsPresent); // a receiver without the keyboard does not make the keyboard present
        Assert.Null(keyboard.Error);
        var route = Assert.Single(routes.GetRoutes(MouseId));
        Assert.Equal(DeviceRouteKind.LogiBolt, route.Kind);
        Assert.Equal(BoltPath, route.EndpointPath);
        Assert.Equal((byte?)2, route.Slot);
        Assert.Equal(RouteState.Online, route.State);
        Assert.Empty(routes.GetRoutes(KeyboardId));
        Assert.Equal(1, hid.CombinedEnumerations);
    }

    [Fact]
    public async Task MouseOnBoltAndKeyboardOnBluetoothAreBothPresentWithDifferentRoutesAndBothReadable()
    {
        var hid = new FakeHidpp().Receiver()
            .Add(BoltPath, 2, FakeHidppDevice.MxMaster3s(percentage: 64))
            .Bluetooth(KeyboardBluetoothPath, 0xB366, FakeHidppDevice.MxMechanical(percentage: 30));
        var monitor = new FakeBluetoothMonitor();
        monitor.Set(KeyboardBluetoothPath, BluetoothLinkState.Connected);
        var routes = new DeviceRouteTable();
        await using var bolt = new LogiBoltReceiverManager(hid, null, LogiBoltReceiverTests.FastOptions);
        await using var discovery = new DeviceDiscoveryService(MxCatalog(), MxRegistry(), hid, null, routes, bolt, null, monitor);

        var snapshot = await discovery.DiscoverAsync();

        Assert.All(snapshot.Devices, device => Assert.True(device.IsPresent && device.IsOnline));
        Assert.Equal(DeviceRouteKind.LogiBolt, Assert.Single(routes.GetRoutes(MouseId)).Kind);
        var keyboardRoute = Assert.Single(routes.GetRoutes(KeyboardId));
        Assert.Equal(DeviceRouteKind.HidppBluetooth, keyboardRoute.Kind);
        Assert.Equal(RouteState.Online, keyboardRoute.State);

        var log = new NullBatterioLog();
        await using var mouse = new RoutedAdapterFactory(LogitechMxMaster3s.Profile, hid, bolt, routes, log)
            .CreateAdapter(BuiltInDevices.CatalogById[MouseId].CreateConfiguration());
        await using var keyboard = new RoutedAdapterFactory(LogitechMxKeysMechanical.Profile, hid, bolt, routes, log)
            .CreateAdapter(BuiltInDevices.CatalogById[KeyboardId].CreateConfiguration());
        Assert.Equal(64, (await mouse.GetBatteryStatus()).Percentage);
        Assert.Equal(30, (await keyboard.GetBatteryStatus()).Percentage);
    }

    [Fact]
    public async Task DeviceOnBluetoothAndBoltGetsBothRoutesAndBluetoothIsPreferredWhileOnline()
    {
        var hid = new FakeHidpp().Receiver()
            .Add(BoltPath, 2, FakeHidppDevice.MxMaster3s(percentage: 64))
            .Bluetooth(MouseBluetoothPath, 0xB034, FakeHidppDevice.MxMaster3s(percentage: 20));
        var monitor = new FakeBluetoothMonitor();
        monitor.Set(MouseBluetoothPath, BluetoothLinkState.Connected);
        var routes = new DeviceRouteTable();
        await using var bolt = new LogiBoltReceiverManager(hid, null, LogiBoltReceiverTests.FastOptions);
        await using var discovery = new DeviceDiscoveryService(MxCatalog(), MxRegistry(), hid, null, routes, bolt, null, monitor);
        await discovery.DiscoverAsync();

        Assert.Equal(2, routes.GetRoutes(MouseId).Count);
        await using var mouse = new RoutedAdapterFactory(LogitechMxMaster3s.Profile, hid, bolt, routes)
            .CreateAdapter(BuiltInDevices.CatalogById[MouseId].CreateConfiguration());
        // Both are online; the Bluetooth route was declared first.
        Assert.Equal(20, (await mouse.GetBatteryStatus()).Percentage);

        // The mouse is switched away from Bluetooth: only the Bolt route is left online and is read next.
        monitor.Set(MouseBluetoothPath, BluetoothLinkState.Disconnected);
        await discovery.DiscoverAsync();
        Assert.Equal(64, (await mouse.GetBatteryStatus()).Percentage);
    }

    [Fact]
    public async Task AFailedEnumerationOfOneQueryDoesNotHideTheOtherDevices()
    {
        var hid = new FakeHidpp().Receiver()
            .Add(BoltPath, 2, FakeHidppDevice.MxMaster3s())
            .FailEnumeration(query => query.UsagePage == 0xFF43 && query.ProductId == 0xB366);
        await using var bolt = new LogiBoltReceiverManager(hid, null, LogiBoltReceiverTests.FastOptions);
        await using var discovery = new DeviceDiscoveryService(MxCatalog(), MxRegistry(), hid, null, new DeviceRouteTable(), bolt);

        var snapshot = await discovery.DiscoverAsync();

        Assert.True(snapshot.Devices.Single(device => device.DeviceId == MouseId).IsPresent);
        var keyboard = snapshot.Devices.Single(device => device.DeviceId == KeyboardId);
        Assert.False(keyboard.IsPresent);
        Assert.Contains("inspect", keyboard.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BurstsOfHidDeviceEventsAreDebouncedIntoOneScan()
    {
        var hid = new FakeHidpp().Receiver().Add(BoltPath, 2, FakeHidppDevice.MxMaster3s());
        var notifier = new FakeDeviceChangeNotifier();
        await using var bolt = new LogiBoltReceiverManager(hid, null, LogiBoltReceiverTests.FastOptions);
        await using var discovery = new DeviceDiscoveryService(
            MxCatalog(), MxRegistry(), hid, null, new DeviceRouteTable(), bolt, notifier, null, SlowFallback);

        await discovery.StartAsync();
        await discovery.RefreshAsync();
        await Task.Delay(150);
        Assert.Equal(1, notifier.Starts);
        Assert.Contains(discovery.CurrentSnapshot.Devices, device => device.IsPresent);
        var before = hid.CombinedEnumerations;

        for (var index = 0; index < 5; index++) notifier.Raise();
        await Eventually(() => hid.CombinedEnumerations == before + 1);
        await Task.Delay(300);

        Assert.Equal(before + 1, hid.CombinedEnumerations);
    }

    [Fact]
    public async Task AReceiverThatIsPluggedInAfterStartupIsPickedUpFromTheEvent()
    {
        var hid = new FakeHidpp();
        var notifier = new FakeDeviceChangeNotifier();
        await using var bolt = new LogiBoltReceiverManager(hid, null, LogiBoltReceiverTests.FastOptions);
        await using var discovery = new DeviceDiscoveryService(
            MxCatalog(), MxRegistry(), hid, null, new DeviceRouteTable(), bolt, notifier, null, SlowFallback);
        await discovery.StartAsync();
        await discovery.RefreshAsync();
        Assert.DoesNotContain(discovery.CurrentSnapshot.Devices, device => device.IsPresent);

        hid.Receiver().Add(BoltPath, 2, FakeHidppDevice.MxMaster3s());
        notifier.Raise();

        await Eventually(() => discovery.CurrentSnapshot.Devices.Single(device => device.DeviceId == MouseId).IsPresent);
    }

    [Fact]
    public async Task TheFallbackScanRunsPeriodicallyWithoutAnyEvent()
    {
        var hid = new FakeHidpp().Receiver().Add(BoltPath, 2, FakeHidppDevice.MxMaster3s());
        await using var bolt = new LogiBoltReceiverManager(hid, null, LogiBoltReceiverTests.FastOptions);
        await using var discovery = new DeviceDiscoveryService(
            MxCatalog(), MxRegistry(), hid, null, new DeviceRouteTable(), bolt, null, null,
            new DeviceDiscoveryOptions { FallbackInterval = TimeSpan.FromMilliseconds(100), EventDebounce = TimeSpan.FromMilliseconds(10) });

        await discovery.StartAsync();

        await Eventually(() => hid.CombinedEnumerations >= 3);
        Assert.Equal(1, hid.OpenedPaths.Count(path => path == BoltPath)); // the open receiver is kept between scans
    }

    [Fact]
    public async Task ABoltDeviceThatWakesUpIsMarkedOnlineWithoutEnumeratingAgain()
    {
        var hid = new FakeHidpp().Receiver().Add(BoltPath, 2, FakeHidppDevice.MxMaster3s(), online: false);
        var routes = new DeviceRouteTable();
        await using var bolt = new LogiBoltReceiverManager(hid, null, LogiBoltReceiverTests.FastOptions);
        await using var discovery = new DeviceDiscoveryService(MxCatalog(), MxRegistry(), hid, null, routes, bolt, null, null, SlowFallback);
        var cameOnline = new System.Collections.Concurrent.ConcurrentQueue<string>();
        discovery.DeviceCameOnline += (_, e) => cameOnline.Enqueue(e.DeviceId);
        await discovery.StartAsync();
        await Eventually(() => discovery.CurrentSnapshot.Devices.Single(device => device.DeviceId == MouseId).IsPresent);
        var mouse = () => discovery.CurrentSnapshot.Devices.Single(device => device.DeviceId == MouseId);
        Assert.False(mouse().IsOnline);
        Assert.Equal(RouteState.Offline, Assert.Single(routes.GetRoutes(MouseId)).State);
        Assert.Empty(cameOnline);
        var enumerations = hid.CombinedEnumerations;

        hid.Notify(BoltPath, 2, 0xB034, online: true);
        await Eventually(() => cameOnline.Count == 1);

        Assert.Equal(new[] { MouseId }, cameOnline.ToArray());
        Assert.True(mouse().IsOnline);
        Assert.Equal(RouteState.Online, Assert.Single(routes.GetRoutes(MouseId)).State);
        Assert.Equal(enumerations, hid.CombinedEnumerations);

        hid.Notify(BoltPath, 2, 0xB034, online: false); // goes back to sleep: present, offline, no event
        await Eventually(() => !mouse().IsOnline);
        Assert.True(mouse().IsPresent);
        Assert.Single(cameOnline);
    }

    [Fact]
    public async Task ABluetoothDeviceThatReconnectsIsMarkedOnlineInPlace()
    {
        var hid = new FakeHidpp().Bluetooth(MouseBluetoothPath, 0xB034, FakeHidppDevice.MxMaster3s());
        var monitor = new FakeBluetoothMonitor();
        monitor.Set(MouseBluetoothPath, BluetoothLinkState.Disconnected);
        var routes = new DeviceRouteTable();
        await using var bolt = new LogiBoltReceiverManager(hid, null, LogiBoltReceiverTests.FastOptions);
        await using var discovery = new DeviceDiscoveryService(MxCatalog(), MxRegistry(), hid, null, routes, bolt, null, monitor, SlowFallback);
        var cameOnline = new System.Collections.Concurrent.ConcurrentQueue<string>();
        discovery.DeviceCameOnline += (_, e) => cameOnline.Enqueue(e.DeviceId);
        await discovery.StartAsync();
        await Eventually(() => discovery.CurrentSnapshot.Devices.Single(device => device.DeviceId == MouseId).IsPresent);
        Assert.Equal(RouteState.Offline, Assert.Single(routes.GetRoutes(MouseId)).State);
        Assert.Equal(1, monitor.Starts);
        var enumerations = hid.CombinedEnumerations;

        monitor.Set(MouseBluetoothPath, BluetoothLinkState.Connected); // raises StatusChanged

        await Eventually(() => cameOnline.Count == 1);
        Assert.Equal(RouteState.Online, Assert.Single(routes.GetRoutes(MouseId)).State);
        Assert.Equal(enumerations, hid.CombinedEnumerations);
    }

    [Fact]
    public async Task AnUnknownBluetoothStateCountsAsPresentButNotOnline()
    {
        var hid = new FakeHidpp().Bluetooth(MouseBluetoothPath, 0xB034, FakeHidppDevice.MxMaster3s());
        var routes = new DeviceRouteTable();
        await using var bolt = new LogiBoltReceiverManager(hid, null, LogiBoltReceiverTests.FastOptions);
        await using var discovery = new DeviceDiscoveryService(MxCatalog(), MxRegistry(), hid, null, routes, bolt);

        var snapshot = await discovery.DiscoverAsync();

        var mouse = snapshot.Devices.Single(device => device.DeviceId == MouseId);
        Assert.True(mouse.IsPresent);
        Assert.False(mouse.IsOnline);
        Assert.Equal(RouteState.Unknown, Assert.Single(routes.GetRoutes(MouseId)).State);
    }

    [Fact]
    public async Task ADeviceThatComesOnlineIsReadImmediatelyWhenItIsSelected()
    {
        using var fixture = new TempDirectory();
        var hid = new FakeHidpp().Receiver().Add(BoltPath, 2, FakeHidppDevice.MxMaster3s(), online: false);
        var mouseDefinition = BuiltInDevices.CatalogById[MouseId];
        var reads = 0;
        var registry = new AdapterRegistry(new[] { new CountingFactory(MouseId, () => Interlocked.Increment(ref reads)) });
        await using var bolt = new LogiBoltReceiverManager(hid, null, LogiBoltReceiverTests.FastOptions);
        await using var polling = new PollingCoordinator(registry);
        await using var discovery = new DeviceDiscoveryService(
            new[] { mouseDefinition }, registry, hid, null, new DeviceRouteTable(), bolt, null, null, SlowFallback);
        await using var store = new JsonUserSettingsStore(Path.Combine(fixture.Path, "settings.json"));
        await using var selection = new DeviceSelectionCoordinator(new[] { mouseDefinition }, store, discovery, polling);

        await selection.StartAsync();
        await Eventually(() => Volatile.Read(ref reads) == 1); // the first poll when polling starts; the default interval is minutes
        await Task.Delay(200);
        Assert.Equal(1, Volatile.Read(ref reads));

        hid.Notify(BoltPath, 2, 0xB034, online: true);

        await Eventually(() => Volatile.Read(ref reads) == 2);
    }

    [Fact]
    public async Task ADeviceThatIsNotSelectedIsNotReadWhenItComesOnline()
    {
        using var fixture = new TempDirectory();
        var settingsPath = Path.Combine(fixture.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath,
            $"{{\"version\":1,\"deviceSelections\":{{\"{MouseId}\":false}},\"knownDevices\":[\"{MouseId}\"]}}");
        var hid = new FakeHidpp().Receiver().Add(BoltPath, 2, FakeHidppDevice.MxMaster3s(), online: false);
        var mouseDefinition = BuiltInDevices.CatalogById[MouseId];
        var reads = 0;
        var registry = new AdapterRegistry(new[] { new CountingFactory(MouseId, () => Interlocked.Increment(ref reads)) });
        await using var bolt = new LogiBoltReceiverManager(hid, null, LogiBoltReceiverTests.FastOptions);
        await using var polling = new PollingCoordinator(registry);
        await using var discovery = new DeviceDiscoveryService(
            new[] { mouseDefinition }, registry, hid, null, new DeviceRouteTable(), bolt, null, null, SlowFallback);
        await using var store = new JsonUserSettingsStore(settingsPath);
        await using var selection = new DeviceSelectionCoordinator(new[] { mouseDefinition }, store, discovery, polling);
        await selection.StartAsync();
        Assert.False(Assert.Single(selection.CurrentSnapshot.Devices).IsSelected);

        hid.Notify(BoltPath, 2, 0xB034, online: true);
        await Eventually(() => discovery.CurrentSnapshot.Devices.Single().IsOnline);
        await Task.Delay(200);

        Assert.Equal(0, Volatile.Read(ref reads));
    }

    private static BuiltInDeviceDefinition[] MxCatalog() =>
        BuiltInDevices.Catalog.Where(device => device.Id is MouseId or KeyboardId).ToArray();

    private static AdapterRegistry MxRegistry() =>
        new(MxCatalog().Select(device => (IBatteryAdapterFactory)new TestFactory(device.AdapterId)));

    private sealed class FakeDeviceChangeNotifier : IDeviceChangeNotifier
    {
        private int _starts;

        public int Starts => Volatile.Read(ref _starts);

        public event EventHandler? Changed;

        public void Start() => Interlocked.Increment(ref _starts);

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
        }
    }

    private sealed class FakeBluetoothMonitor : IBluetoothConnectionMonitor
    {
        private readonly ConcurrentDictionary<string, BluetoothLinkState> _states = new(StringComparer.OrdinalIgnoreCase);
        private int _starts;

        public int Starts => Volatile.Read(ref _starts);

        public event EventHandler? StatusChanged;

        public void Start() => Interlocked.Increment(ref _starts);

        public BluetoothLinkState GetState(string hidDevicePath) =>
            _states.TryGetValue(hidDevicePath, out var state) ? state : BluetoothLinkState.Unknown;

        public void Set(string path, BluetoothLinkState state)
        {
            _states[path] = state;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
        }
    }

    private sealed class CountingFactory(string adapterId, Action onRead) : IBatteryAdapterFactory
    {
        public string AdapterId { get; } = adapterId;

        public void ValidateSettings(JsonElement settings) { }

        public IBatteryAdapter CreateAdapter(DeviceConfiguration device) => new CountingAdapter(onRead);
    }

    private sealed class CountingAdapter(Action onRead) : IBatteryAdapter
    {
        public Task<BatteryReading> GetBatteryStatus(CancellationToken cancellationToken = default)
        {
            onRead();
            return Task.FromResult(new BatteryReading(50, BatteryChargeState.NotCharging));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static BuiltInDeviceDefinition Definition(string id, ushort vendorId) =>
        Definition(id, Query(vendorId));

    private static BuiltInDeviceDefinition Definition(string id, params HidCollectionQuery[] queries) => new()
    {
        Id = id,
        Name = id,
        AdapterId = id,
        Kind = DeviceKind.Mouse,
        Routes = queries.Select(query => (DeviceRouteDescriptor)new UsbHidRouteDescriptor(DeviceRouteKind.UsbReceiver, query)).ToArray()
    };

    private static HidCollectionQuery FirstQuery(BuiltInDeviceDefinition definition) =>
        ((UsbHidRouteDescriptor)definition.Routes[0]).Query;

    private static HidCollectionQuery Query(ushort productId) => new()
    {
        VendorId = 0x046D,
        ProductId = productId,
        InterfaceNumber = 1,
        UsagePage = 0xFF00,
        Usage = 1,
        MinimumReportLength = 20,
        RequiredReportId = 0x11
    };

    private static HidCollectionInfo Collection(HidCollectionQuery query) => new()
    {
        DevicePath = $"path-{query.VendorId:X4}",
        VendorId = query.VendorId,
        ProductId = query.ProductId,
        InterfaceNumber = query.InterfaceNumber,
        UsagePage = query.UsagePage,
        Usage = query.Usage,
        InputReportLength = query.MinimumReportLength,
        OutputReportLength = query.MinimumReportLength,
        SupportedReportIds = new HashSet<byte> { query.RequiredReportId!.Value }
    };

    private static async Task Eventually(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private sealed class FakeHidProvider : IHidCollectionProvider
    {
        public IReadOnlyList<HidCollectionInfo> Collections { get; set; } = Array.Empty<HidCollectionInfo>();

        public bool ThrowOnEnumerate { get; set; }

        public HashSet<ushort> ThrowForProductIds { get; } = new();

        public int Enumerations { get; private set; }

        public List<ushort> EnumeratedProductIds { get; } = new();

        public int Opens { get; private set; }

        public IReadOnlyList<HidCollectionInfo> Enumerate(HidCollectionQuery query)
        {
            Enumerations++;
            EnumeratedProductIds.Add(query.ProductId);
            if (ThrowOnEnumerate || ThrowForProductIds.Contains(query.ProductId))
            {
                throw new InvalidOperationException("enumeration failed");
            }

            return Collections;
        }

        public ValueTask<IHidReportTransport> OpenAsync(HidCollectionInfo collection, CancellationToken cancellationToken = default)
        {
            Opens++;
            return ValueTask.FromException<IHidReportTransport>(new InvalidOperationException("Discovery must not open HID transports."));
        }
    }

    private sealed class TestFactory(string adapterId) : IBatteryAdapterFactory
    {
        public string AdapterId { get; } = adapterId;

        public void ValidateSettings(JsonElement settings) { }

        public IBatteryAdapter CreateAdapter(DeviceConfiguration device) => new TestAdapter();
    }

    private sealed class TestAdapter : IBatteryAdapter
    {
        public Task<BatteryReading> GetBatteryStatus(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BatteryReading(50, BatteryChargeState.NotCharging));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingLog : IBatterioLog
    {
        public List<string> Messages { get; } = new();

        public void Log(BatterioLogLevel level, string message, Exception? exception = null) => Messages.Add(message);
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

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
            Collections = new[] { Collection(first.PresenceQueries[0]), Collection(first.PresenceQueries[0]) }
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
        var provider = new FakeHidProvider { Collections = new[] { Collection(definition.PresenceQueries[0]) } };
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
    public async Task DiscoveryChecksPresenceQueriesInOrderAndStopsAfterTheFirstMatch()
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
            Assert.Equal(new ushort[] { 0xC0A8 }, wiredProvider.EnumeratedProductIds);
            Assert.Equal(0, wiredProvider.Opens);
        }

        var receiverProvider = new FakeHidProvider { Collections = new[] { Collection(receiverQuery) } };
        await using var receiverDiscovery = new DeviceDiscoveryService(new[] { definition }, registry, receiverProvider);
        var receiverSnapshot = await receiverDiscovery.DiscoverAsync();
        Assert.True(Assert.Single(receiverSnapshot.Devices).IsPresent);
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
        var collection = Collection(definition.PresenceQueries[0]);
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
        var ownedCollection = Collection(owned.PresenceQueries[0]);
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

    private static BuiltInDeviceDefinition Definition(string id, ushort vendorId) =>
        Definition(id, Query(vendorId));

    private static BuiltInDeviceDefinition Definition(string id, params HidCollectionQuery[] queries) => new()
    {
        Id = id,
        Name = id,
        AdapterId = id,
        PresenceQueries = queries
    };

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

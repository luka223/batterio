using System.Collections.Concurrent;
using System.Text.Json;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

/// <summary>
/// A device whose routes all report it gone is shown as Offline after a short grace, without any request to it. Two rules keep that from
/// racing the polls: a read that began before the mark is dropped, and a read that succeeded after the "gone" signal cancels the mark.
/// </summary>
public sealed class OfflineMarkTests
{
    private const string SuperstrikeId = "logitech-g-pro-x2-superstrike";
    private const string OmniId = "steelseries-arctis-nova-pro-omni";
    private const string KeychronId = "keychron-q1-he";

    private static readonly DeviceDiscoveryOptions SlowFallback = new()
    {
        FallbackInterval = TimeSpan.FromHours(1),
        EventDebounce = TimeSpan.FromMilliseconds(20)
    };

    // ----- PollingCoordinator -----

    [Fact]
    public async Task AMarkShowsOfflineWithoutAnErrorAndKeepsTheReserveBattery()
    {
        var adapter = new ScriptedAdapter { Default = _ => Task.FromResult(WithReserve(70, 40)) };
        await using var polling = await StartPollingAsync(adapter);
        await Eventually(() => Percentage(polling) == 70);

        Assert.True(polling.MarkDeviceOffline("device", Token(polling)));

        var state = polling.States.Single();
        Assert.Equal(BatteryChargeState.Offline, state.State);
        Assert.Null(state.Error);
        Assert.Equal(40, state.Reading.Reserve?.Percentage);
        Assert.Equal(1, adapter.Calls); // nothing was sent to the device
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("offline")]
    public async Task AReadThatStartedBeforeTheMarkIsDropped(string outcome)
    {
        var adapter = new ScriptedAdapter();
        await using var polling = await StartPollingAsync(adapter);
        await Eventually(() => Percentage(polling) == 50);

        var pending = adapter.ThenPending();
        var refresh = polling.RefreshDeviceAsync("device");
        await Eventually(() => adapter.Calls == 2);
        Assert.True(polling.MarkDeviceOffline("device", Token(polling)));

        switch (outcome)
        {
            case "success": pending.SetResult(Reading(90)); break;
            case "failure": pending.SetException(new IOException("receiver disconnected")); break;
            default: pending.SetException(new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline, "asleep")); break;
        }

        await refresh;
        var state = polling.States.Single();
        Assert.Equal(BatteryChargeState.Offline, state.State);
        Assert.Null(state.Error);
    }

    [Fact]
    public async Task AReadThatTimesOutAfterTheMarkIsDroppedToo()
    {
        var adapter = new ScriptedAdapter();
        await using var polling = await StartPollingAsync(adapter, timeoutSeconds: 1);
        await Eventually(() => Percentage(polling) == 50);

        adapter.ThenPending(); // never answers
        var refresh = polling.RefreshDeviceAsync("device");
        await Eventually(() => adapter.Calls == 2);
        Assert.True(polling.MarkDeviceOffline("device", Token(polling)));
        await refresh;

        Assert.Null(polling.States.Single().Error); // not "The adapter timed out."
    }

    [Fact]
    public async Task AReadThatStartsAfterTheMarkIsTrusted()
    {
        var adapter = new ScriptedAdapter();
        await using var polling = await StartPollingAsync(adapter);
        await Eventually(() => Percentage(polling) == 50);
        Assert.True(polling.MarkDeviceOffline("device", Token(polling)));

        adapter.Then(_ => Task.FromResult(Reading(65)));
        await polling.RefreshDeviceAsync("device");

        Assert.Equal(65, Percentage(polling));
    }

    [Fact]
    public async Task ASuccessfulReadAfterTheGoneSignalCancelsTheMark()
    {
        var adapter = new ScriptedAdapter();
        await using var polling = await StartPollingAsync(adapter);
        await Eventually(() => Percentage(polling) == 50);
        var token = Token(polling);

        adapter.Then(_ => Task.FromResult(Reading(55)));
        await polling.RefreshDeviceAsync("device");

        Assert.False(polling.MarkDeviceOffline("device", token));
        Assert.Equal(55, Percentage(polling));
    }

    [Fact]
    public async Task AFailedReadAfterTheGoneSignalIsReplacedByTheMark()
    {
        var adapter = new ScriptedAdapter();
        await using var polling = await StartPollingAsync(adapter);
        await Eventually(() => Percentage(polling) == 50);
        var token = Token(polling);

        adapter.Then(_ => Task.FromException<BatteryReading>(new IOException("receiver disconnected")));
        await polling.RefreshDeviceAsync("device");
        Assert.NotNull(polling.States.Single().Error);

        Assert.True(polling.MarkDeviceOffline("device", token));
        Assert.Equal(BatteryChargeState.Offline, polling.States.Single().State);
        Assert.Null(polling.States.Single().Error);
    }

    [Fact]
    public async Task ATokenOfARetiredWorkerOrAnUnknownDeviceMarksNothing()
    {
        var adapter = new ScriptedAdapter();
        await using var polling = await StartPollingAsync(adapter);
        await Eventually(() => Percentage(polling) == 50);
        var token = Token(polling);

        // A configuration change replaces the worker, which starts over from its own first read.
        await polling.StartDevicesAsync([Device(name: "renamed")]);
        await Eventually(() => Percentage(polling) == 50);
        Assert.False(polling.MarkDeviceOffline("device", token));
        Assert.Equal(50, Percentage(polling));

        Assert.Null(polling.GetOfflineMarkToken("unknown"));
        Assert.False(polling.MarkDeviceOffline("unknown", token));
    }

    [Fact]
    public async Task AStoppedCoordinatorRefusesAMark()
    {
        var polling = await StartPollingAsync(new ScriptedAdapter());
        await Eventually(() => Percentage(polling) == 50);
        var token = Token(polling);
        await polling.StopAsync();

        Assert.Throws<ObjectDisposedException>(() => polling.MarkDeviceOffline("device", token));
        await polling.DisposeAsync();
    }

    [Fact]
    public async Task AnOfflineDeviceThatWasReadBeforeIsPlainOfflineButBeforeItsFirstReadItKeepsTheHint()
    {
        var offline = new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline, "no battery-monitor firmware?");
        var adapter = new ScriptedAdapter { Default = _ => Task.FromException<BatteryReading>(offline) };
        await using var polling = await StartPollingAsync(adapter);
        await Eventually(() => polling.States.Single().Error is not null);

        // Before any success the message is the only hint at a setup problem, and a mark keeps it too.
        Assert.Equal(offline.Message, polling.States.Single().Error);
        Assert.True(polling.MarkDeviceOffline("device", Token(polling)));
        Assert.Equal(offline.Message, polling.States.Single().Error);

        // After a success the device is just asleep: Offline, no error, reserve kept.
        adapter.Then(_ => Task.FromResult(WithReserve(70, 40)));
        await polling.RefreshDeviceAsync("device");
        await polling.RefreshDeviceAsync("device");

        var state = polling.States.Single();
        Assert.Equal(BatteryChargeState.Offline, state.State);
        Assert.Null(state.Error);
        Assert.Equal(40, state.Reading.Reserve?.Percentage);
    }

    // ----- Discovery -----

    [Fact]
    public async Task ADeviceIsReportedOfflineOnlyWhenEveryRouteIsAndTheSignalIsRaisedOncePerTransition()
    {
        var receiver = UsbRoutesTests.CollectionFor(LogitechGProX2Superstrike.ReceiverQuery, "receiver");
        var cable = UsbRoutesTests.CollectionFor(LogitechGProX2Superstrike.WiredQuery, "cable");
        var source = new UsbRoutesTests.FakeStateSource(null, collection => collection.DevicePath == "receiver");
        var hid = new ListProvider { Collections = [receiver, cable] };
        await using var discovery = new DeviceDiscoveryService(
            [BuiltInDevices.CatalogById[SuperstrikeId]], Registry(SuperstrikeId), hid, null, null, null, null, null, SlowFallback, [source]);
        var wentOffline = new ConcurrentQueue<string>();
        var published = 0;
        discovery.DeviceWentOffline += (_, e) => wentOffline.Enqueue(e.DeviceId);
        discovery.SnapshotChanged += (_, _) => Interlocked.Increment(ref published);
        await discovery.DiscoverAsync();
        var publishedAfterFirstScan = Volatile.Read(ref published);

        // Receiver gone, but the cable (state unknown) still works: not "gone".
        source.State = RouteState.Offline;
        Assert.False((await discovery.DiscoverAsync()).Devices.Single().IsReportedOffline);
        Assert.Empty(wentOffline);

        // Cable unplugged: every route is Offline. That is announced, but it does not republish the snapshot: the device stays present
        // and was never online, and consumers read the current snapshot anyway.
        hid.Collections = [receiver];
        Assert.True((await discovery.DiscoverAsync()).Devices.Single().IsReportedOffline);
        Assert.True(discovery.CurrentSnapshot.Devices.Single().IsReportedOffline);
        Assert.Equal(SuperstrikeId, Assert.Single(wentOffline));
        await discovery.DiscoverAsync();
        Assert.Single(wentOffline);
        Assert.Equal(publishedAfterFirstScan, Volatile.Read(ref published));

        // Unknown in between, then gone again: a second transition.
        source.State = null;
        Assert.False((await discovery.DiscoverAsync()).Devices.Single().IsReportedOffline);
        source.State = RouteState.Offline;
        await discovery.DiscoverAsync();
        Assert.Equal(2, wentOffline.Count);
    }

    [Fact]
    public async Task TheFirstScanAnnouncesADeviceThatIsAlreadyGone()
    {
        var source = new UsbRoutesTests.FakeStateSource(RouteState.Offline);
        var hid = new ListProvider { Collections = [UsbRoutesTests.CollectionFor(LogitechGProX2Superstrike.ReceiverQuery, "receiver")] };
        await using var discovery = new DeviceDiscoveryService(
            [BuiltInDevices.CatalogById[SuperstrikeId]], Registry(SuperstrikeId), hid, null, null, null, null, null, SlowFallback, [source]);
        var wentOffline = new ConcurrentQueue<string>();
        discovery.DeviceWentOffline += (_, e) => wentOffline.Enqueue(e.DeviceId);

        await discovery.DiscoverAsync();

        Assert.Single(wentOffline);
    }

    [Fact]
    public async Task ADeviceWhoseGoneSignalIsNotTrustedIsNeverAnnounced()
    {
        var definition = BuiltInDevices.CatalogById[SuperstrikeId] with { ShowsOfflineOnLinkLoss = false };
        var source = new UsbRoutesTests.FakeStateSource(RouteState.Offline);
        var hid = new ListProvider { Collections = [UsbRoutesTests.CollectionFor(LogitechGProX2Superstrike.ReceiverQuery, "receiver")] };
        await using var discovery = new DeviceDiscoveryService(
            [definition], Registry(SuperstrikeId), hid, null, null, null, null, null, SlowFallback, [source]);
        var wentOffline = new ConcurrentQueue<string>();
        discovery.DeviceWentOffline += (_, e) => wentOffline.Enqueue(e.DeviceId);

        var snapshot = await discovery.DiscoverAsync();

        Assert.True(snapshot.Devices.Single().IsReportedOffline);
        Assert.Empty(wentOffline);
        Assert.False(BuiltInDevices.CatalogById[LogitechMxMaster3s.AdapterId].ShowsOfflineOnLinkLoss);
        Assert.False(BuiltInDevices.CatalogById[LogitechMxKeysMechanical.AdapterId].ShowsOfflineOnLinkLoss);
    }

    // ----- DeviceSelectionCoordinator -----

    [Fact]
    public async Task AGoneDeviceIsShownOfflineAfterTheGraceWithoutBeingRead()
    {
        await using var rig = await Rig.StartAsync(RouteState.Online);
        await Eventually(() => rig.Percentage == 50);
        var calls = rig.Adapter.Calls;

        await rig.SetReceiverAsync(RouteState.Offline);
        await Task.Delay(Rig.Grace / 2);
        Assert.Equal(50, rig.Percentage); // still inside the grace

        await Eventually(() => rig.State == BatteryChargeState.Offline);
        Assert.Null(rig.Polling.States.Single().Error);
        Assert.Equal(calls, rig.Adapter.Calls);
    }

    [Fact]
    public async Task ComingBackInsideTheGraceMarksNothing()
    {
        await using var rig = await Rig.StartAsync(RouteState.Online);
        await Eventually(() => rig.Percentage == 50);

        await rig.SetReceiverAsync(RouteState.Offline);
        await rig.SetReceiverAsync(RouteState.Online);
        await Task.Delay(Rig.Grace * 2);

        Assert.DoesNotContain(BatteryChargeState.Offline, rig.ObservedStates);
        Assert.Equal(50, rig.Percentage);
    }

    [Fact]
    public async Task ADeviceThatIsNotSelectedIsNotMarked()
    {
        await using var rig = await Rig.StartAsync(RouteState.Online);
        await Eventually(() => rig.Percentage == 50);
        await rig.Selection.SetDeviceSelectedAsync(SuperstrikeId, false);

        await rig.SetReceiverAsync(RouteState.Offline);
        await Task.Delay(Rig.Grace * 2);

        Assert.Empty(rig.Polling.States);
    }

    [Fact]
    public async Task UnpluggingTheCableWhileTheMouseAnswersKeepsTheBatteryShown()
    {
        // On its cable the receiver route is Offline (the mouse left it for the cable). Unplugging leaves only that route: the device is
        // "gone", and the route change reads it at once. That read succeeds, which proves the route state wrong, so no mark follows.
        await using var rig = await Rig.StartAsync(RouteState.Offline, withCable: true);
        await Eventually(() => rig.Percentage == 50);
        rig.Adapter.Default = _ => Task.FromResult(Reading(48));

        await rig.UnplugCableAsync();
        await Eventually(() => rig.Percentage == 48);
        await Task.Delay(Rig.Grace * 2);

        Assert.Equal(48, rig.Percentage);
    }

    [Fact]
    public async Task UnpluggingTheCableWhileTheMouseIsUnreachableShowsOffline()
    {
        await using var rig = await Rig.StartAsync(RouteState.Offline, withCable: true);
        await Eventually(() => rig.Percentage == 50);
        rig.Adapter.Default = _ => Task.FromException<BatteryReading>(
            new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline, "no answer"));

        await rig.UnplugCableAsync();

        await Eventually(() => rig.State == BatteryChargeState.Offline);
        await Task.Delay(Rig.Grace * 2);
        Assert.Equal(BatteryChargeState.Offline, rig.State);
        Assert.Null(rig.Polling.States.Single().Error);
    }

    [Fact]
    public async Task GoneAnsweredAndGoneAgainInsideOneGraceStillMarks()
    {
        await using var rig = await Rig.StartAsync(RouteState.Online, grace: TimeSpan.FromMilliseconds(600));
        await Eventually(() => rig.Percentage == 50);

        await rig.SetReceiverAsync(RouteState.Offline);
        await rig.Polling.RefreshDeviceAsync(SuperstrikeId); // answered: the first signal's token is spent
        await rig.SetReceiverAsync(null);
        await rig.SetReceiverAsync(RouteState.Offline); // the latest signal's token counts

        await Eventually(() => rig.State == BatteryChargeState.Offline);
    }

    [Fact]
    public async Task TheReadAfterComingBackIsTrustedOverTheMark()
    {
        await using var rig = await Rig.StartAsync(RouteState.Online);
        await Eventually(() => rig.Percentage == 50);
        await rig.SetReceiverAsync(RouteState.Offline);
        await Eventually(() => rig.State == BatteryChargeState.Offline);
        var calls = rig.Adapter.Calls;

        rig.Adapter.Default = _ => Task.FromResult(Reading(47));
        await rig.SetReceiverAsync(RouteState.Online);

        await Eventually(() => rig.Percentage == 47);
        Assert.Equal(calls + 1, rig.Adapter.Calls);
    }

    [Fact]
    public async Task ShuttingDownDuringTheGraceLogsNoWarning()
    {
        var rig = await Rig.StartAsync(RouteState.Online);
        await Eventually(() => rig.Percentage == 50);

        await rig.SetReceiverAsync(RouteState.Offline);
        await rig.DisposeAsync();
        await Task.Delay(Rig.Grace * 2);

        Assert.DoesNotContain(rig.Log.Entries, entry => entry.Level >= BatterioLogLevel.Warning);
    }

    [Fact]
    public async Task TheKeychronFallingSilentIsShownOffline()
    {
        await using var rig = await Rig.StartAsync(RouteState.Online, deviceId: KeychronId);
        await Eventually(() => rig.Percentage == 50);

        await rig.SetReceiverAsync(RouteState.Offline); // the listener's silence timeout makes its route Offline

        await Eventually(() => rig.State == BatteryChargeState.Offline);
    }

    // ----- End to end: the Omni hub's own events -----

    [Fact]
    public async Task SwitchingTheOmniOffShowsItOfflineWithItsReserveAndSwitchingItOnReadsItOnce()
    {
        var hid = new SteelSeriesHubMonitorTests.EventProvider { Collections = [OmniStatus(), OmniEvents()] };
        await using var monitor = new SteelSeriesHubMonitor(hid);
        var adapter = new ScriptedAdapter { Default = _ => Task.FromResult(WithReserve(70, 40)) };
        var registry = new AdapterRegistry([new SingleFactory(OmniId, adapter)]);
        var routes = new DeviceRouteTable();
        var definition = BuiltInDevices.CatalogById[OmniId];
        await using var polling = new PollingCoordinator(registry);
        await using var discovery = new DeviceDiscoveryService(
            [definition], registry, hid, null, routes, null, null, null, SlowFallback, [monitor]);
        using var directory = new TempDirectory();
        await using var store = new JsonUserSettingsStore(Path.Combine(directory.Path, "settings.json"));
        await using var selection = new DeviceSelectionCoordinator(
            [definition], store, discovery, polling, null, routes, TimeSpan.FromMilliseconds(50), Rig.Grace);
        await selection.StartAsync();
        await Eventually(() => polling.States.SingleOrDefault()?.Percentage == 70);
        await Eventually(() => hid.Opens == 1);
        hid.Push(OmniFrame(0x08)); // on
        await Eventually(() => adapter.Calls >= 2);
        await Task.Delay(200);
        var calls = adapter.Calls;

        hid.Push(OmniFrame(0x04)); // off
        await Eventually(() => polling.States.Single().State == BatteryChargeState.Offline);
        Assert.Equal(40, polling.States.Single().Reading.Reserve?.Percentage);
        Assert.Equal(calls, adapter.Calls);

        hid.Push(OmniFrame(0x08)); // on again
        await Eventually(() => polling.States.Single().Percentage == 70);
        await Task.Delay(200);
        Assert.Equal(calls + 1, adapter.Calls);
        Assert.Empty(hid.Writes);
    }

    // ----- helpers -----

    private static BatteryReading Reading(int percentage) => new(percentage, BatteryChargeState.NotCharging);

    private static BatteryReading WithReserve(int percentage, int reserve) =>
        Reading(percentage) with { Reserve = Reading(reserve) };

    private static int? Percentage(PollingCoordinator polling) => polling.States.SingleOrDefault()?.Percentage;

    private static OfflineMarkToken Token(PollingCoordinator polling) =>
        polling.GetOfflineMarkToken("device") ?? throw new InvalidOperationException("The device is not polled.");

    private static DeviceConfiguration Device(int timeoutSeconds = 30, string name = "device") => new()
    {
        Id = "device",
        Name = name,
        AdapterId = "test",
        AdapterSettings = JsonDocument.Parse("{}").RootElement,
        PollIntervalSeconds = 3600,
        TimeoutSeconds = timeoutSeconds
    };

    private static async Task<PollingCoordinator> StartPollingAsync(ScriptedAdapter adapter, int timeoutSeconds = 30)
    {
        var polling = new PollingCoordinator(new AdapterRegistry([new SingleFactory("test", adapter)]));
        await polling.StartDevicesAsync([Device(timeoutSeconds)]);
        return polling;
    }

    private static AdapterRegistry Registry(string id) => new([new SingleFactory(id, new ScriptedAdapter())]);

    private static byte[] OmniFrame(byte state)
    {
        var frame = new byte[64];
        frame[0] = 0x07;
        frame[1] = 0xB5;
        frame[2] = 0x01;
        frame[4] = state;
        return frame;
    }

    private static HidCollectionInfo OmniStatus() => new()
    {
        DevicePath = @"\\?\hid#vid_1038&pid_2290&mi_03&col01#a&2a1224a3&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
        VendorId = 0x1038,
        ProductId = 0x2290,
        InterfaceNumber = 3,
        UsagePage = 0xFFC0,
        Usage = 0x0001,
        InputReportLength = 64,
        OutputReportLength = 64,
        SupportedReportIds = new HashSet<byte> { 0x01 }
    };

    private static HidCollectionInfo OmniEvents() => OmniStatus() with
    {
        DevicePath = @"\\?\hid#vid_1038&pid_2290&mi_03&col02#a&2a1224a3&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}",
        UsagePage = 0xFF00,
        SupportedReportIds = new HashSet<byte> { 0x07 }
    };

    private static async Task Eventually(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        Assert.True(condition(), "Condition was not reached before the timeout.");
    }

    /// <summary>Answers each read with the next scripted step, or <see cref="Default"/> when none is left.</summary>
    private sealed class ScriptedAdapter : IBatteryAdapter
    {
        private readonly ConcurrentQueue<Func<CancellationToken, Task<BatteryReading>>> _script = new();
        private int _calls;

        public Func<CancellationToken, Task<BatteryReading>> Default { get; set; } = _ => Task.FromResult(Reading(50));

        public int Calls => Volatile.Read(ref _calls);

        public void Then(Func<CancellationToken, Task<BatteryReading>> step) => _script.Enqueue(step);

        public TaskCompletionSource<BatteryReading> ThenPending()
        {
            var pending = new TaskCompletionSource<BatteryReading>(TaskCreationOptions.RunContinuationsAsynchronously);
            Then(cancellationToken => pending.Task.WaitAsync(cancellationToken));
            return pending;
        }

        public Task<BatteryReading> GetBatteryStatus(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return _script.TryDequeue(out var step) ? step(cancellationToken) : Default(cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SingleFactory(string adapterId, IBatteryAdapter adapter) : IBatteryAdapterFactory
    {
        public string AdapterId => adapterId;

        public void ValidateSettings(JsonElement settings) { }

        public IBatteryAdapter CreateAdapter(DeviceConfiguration device) => adapter;
    }

    private sealed class ListProvider : IHidCollectionProvider
    {
        public IReadOnlyList<HidCollectionInfo> Collections { get; set; } = [];

        public IReadOnlyList<HidCollectionInfo> Enumerate(HidCollectionQuery query) => Collections;

        public ValueTask<IHidReportTransport> OpenAsync(HidCollectionInfo collection, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IHidReportTransport>(new InvalidOperationException("The tests never open HID transports."));
    }

    private sealed class CapturingLog : IBatterioLog
    {
        public ConcurrentQueue<(BatterioLogLevel Level, string Message)> Entries { get; } = new();

        public void Log(BatterioLogLevel level, string message, Exception? exception = null) => Entries.Enqueue((level, message));
    }

    /// <summary>
    /// Discovery, polling and selection wired as in the app for one device whose receiver (or Link dongle) route state the test sets, the
    /// way a monitor would report it.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        public static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(300);

        private readonly TempDirectory _directory = new();
        private readonly DeviceDiscoveryService _discovery;
        private readonly JsonUserSettingsStore _store;
        private readonly UsbRoutesTests.FakeStateSource _source;
        private readonly ListProvider _hid;
        private readonly HidCollectionInfo _receiver;
        private int _disposed;

        private Rig(string deviceId, RouteState? receiverState, bool withCable, TimeSpan grace)
        {
            var definition = BuiltInDevices.CatalogById[deviceId];
            _receiver = deviceId == KeychronId
                ? UsbRoutesTests.CollectionFor(KeychronQ1He.LinkRawHidQuery, "receiver")
                : UsbRoutesTests.CollectionFor(LogitechGProX2Superstrike.ReceiverQuery, "receiver");
            _hid = new ListProvider
            {
                Collections = withCable
                    ? [_receiver, UsbRoutesTests.CollectionFor(LogitechGProX2Superstrike.WiredQuery, "cable")]
                    : [_receiver]
            };
            _source = new UsbRoutesTests.FakeStateSource(receiverState, collection => collection.DevicePath == "receiver");
            var registry = new AdapterRegistry([new SingleFactory(deviceId, Adapter)]);
            var routes = new DeviceRouteTable();
            Polling = new PollingCoordinator(registry);
            _discovery = new DeviceDiscoveryService([definition], registry, _hid, null, routes, null, null, null, SlowFallback, [_source]);
            _store = new JsonUserSettingsStore(Path.Combine(_directory.Path, "settings.json"));
            Selection = new DeviceSelectionCoordinator(
                [definition], _store, _discovery, Polling, Log, routes, TimeSpan.FromMilliseconds(50), grace);
            Polling.StateChanged += (_, e) => ObservedStates.Enqueue(e.State.State);
        }

        public ScriptedAdapter Adapter { get; } = new();

        public PollingCoordinator Polling { get; }

        public DeviceSelectionCoordinator Selection { get; }

        public CapturingLog Log { get; } = new();

        public ConcurrentQueue<BatteryChargeState> ObservedStates { get; } = new();

        public int? Percentage => Polling.States.SingleOrDefault()?.Percentage;

        public BatteryChargeState? State => Polling.States.SingleOrDefault()?.State;

        public static async Task<Rig> StartAsync(
            RouteState? receiverState,
            bool withCable = false,
            TimeSpan? grace = null,
            string deviceId = SuperstrikeId)
        {
            var rig = new Rig(deviceId, receiverState, withCable, grace ?? Grace);
            await rig.Selection.StartAsync();

            // Let the startup reads finish (the first poll, and the came-online read when the receiver starts Online): a success that
            // lands after a test's "gone" signal would rightly cancel the mark.
            await Eventually(() => rig.Percentage is not null);
            await Task.Delay(250);
            rig.ObservedStates.Clear();
            return rig;
        }

        /// <summary>The receiver's monitor reports a new state; discovery re-resolves the routes as on its StateChanged event.</summary>
        public async Task SetReceiverAsync(RouteState? state)
        {
            _source.State = state;
            await _discovery.DiscoverAsync();
        }

        public async Task UnplugCableAsync()
        {
            _hid.Collections = [_receiver];
            await Selection.RefreshDiscoveryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await Selection.DisposeAsync();
            await Polling.DisposeAsync();
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

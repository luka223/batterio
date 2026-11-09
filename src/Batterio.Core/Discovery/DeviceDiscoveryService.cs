using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>Timing of <see cref="DeviceDiscoveryService"/>; tests shorten it.</summary>
public sealed record DeviceDiscoveryOptions
{
    /// <summary>A full scan runs at startup, on events, and at least this often in case an event was missed.</summary>
    public TimeSpan FallbackInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Events (a receiver plug-in produces a burst of interface notifications) are grouped into one scan.</summary>
    public TimeSpan EventDebounce { get; init; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// Finds which built-in devices are present and how they can be reached.
/// </summary>
/// <remarks>
/// A full scan enumerates every HID collection the catalog cares about in one pass, lets the Logi Bolt receivers settle, resolves each
/// device's route descriptors into <see cref="ResolvedRoute"/>s and publishes them to the <see cref="DeviceRouteTable"/>.
/// Full scans run at startup, when HID devices come or go, when a Bolt receiver's pairing changes or fails, and every
/// <see cref="DeviceDiscoveryOptions.FallbackInterval"/>. Link changes (a Bolt device connects, a Bluetooth link drops) only re-resolve the
/// route states from the last enumeration, so a device that wakes up is noticed within moments without enumerating anything.
/// </remarks>
public sealed class DeviceDiscoveryService : IDeviceDiscoveryService
{
    private readonly IReadOnlyList<BuiltInDeviceDefinition> _catalog;
    private readonly AdapterRegistry _registry;
    private readonly IHidCollectionProvider _provider;
    private readonly IBatterioLog _log;
    private readonly DeviceRouteTable _routeTable;
    private readonly LogiBoltReceiverManager? _bolt;
    private readonly bool _ownsBolt;
    private readonly IDeviceChangeNotifier _changeNotifier;
    private readonly IBluetoothConnectionMonitor _bluetooth;
    private readonly IReadOnlyList<IUsbRouteStateSource> _usbSources;
    private readonly DeviceRouteResolver _resolver;
    private readonly DeviceDiscoveryOptions _options;
    private readonly IReadOnlyList<HidCollectionQuery> _queries;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceDiscoveryState> _states;
    private CancellationTokenSource? _lifetime;
    private Task? _loop;
    private TaskCompletionSource<bool> _refreshSignal = NewSignal();
    private TaskCompletionSource<bool>? _pendingRefresh;
    private DeviceDiscoverySnapshot _snapshot;
    private ScanData? _lastScan;
    private long _revision;
    private long _debounceVersion;
    private bool _subscribed;
    private bool _stopped;
    private int _disposed;

    public DeviceDiscoveryService(
        IEnumerable<BuiltInDeviceDefinition> catalog,
        AdapterRegistry registry,
        IHidCollectionProvider provider,
        IBatterioLog? log = null,
        DeviceRouteTable? routeTable = null,
        LogiBoltReceiverManager? receivers = null,
        IDeviceChangeNotifier? changeNotifier = null,
        IBluetoothConnectionMonitor? bluetooth = null,
        DeviceDiscoveryOptions? options = null,
        IEnumerable<IUsbRouteStateSource>? usbSources = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _log = log ?? new NullBatterioLog();
        _routeTable = routeTable ?? new DeviceRouteTable();
        _changeNotifier = changeNotifier ?? new NullDeviceChangeNotifier();
        _bluetooth = bluetooth ?? new NullBluetoothConnectionMonitor();
        _options = options ?? new DeviceDiscoveryOptions();
        _usbSources = usbSources?.ToArray() ?? [];

        _catalog = catalog
            .Where(device => _registry.TryGetFactory(device.AdapterId, out _))
            .ToArray();
        _states = _catalog.ToDictionary(
            device => device.Id,
            device => new DeviceDiscoveryState { Device = device },
            StringComparer.OrdinalIgnoreCase);

        var routes = _catalog.SelectMany(device => device.Routes).ToArray();
        if (routes.OfType<LogiBoltRouteDescriptor>().Any())
        {
            _bolt = receivers;
            if (_bolt is null)
            {
                _bolt = new LogiBoltReceiverManager(_provider, _log);
                _ownsBolt = true;
            }
        }

        _resolver = new DeviceRouteResolver(_bolt, _bluetooth, _usbSources);
        _queries = routes.SelectMany(DeviceRouteResolver.QueriesOf)
            .Concat(_usbSources.SelectMany(source => source.Queries))
            .Distinct()
            .ToArray();
        _snapshot = new DeviceDiscoverySnapshot(0, OrderedStates());
    }

    public DeviceDiscoverySnapshot CurrentSnapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public event EventHandler<DeviceDiscoverySnapshotChangedEventArgs>? SnapshotChanged;

    public event EventHandler<DeviceCameOnlineEventArgs>? DeviceCameOnline;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_gate)
        {
            if (_stopped)
            {
                throw new ObjectDisposedException(nameof(DeviceDiscoveryService));
            }

            if (_loop is not null)
            {
                return Task.CompletedTask;
            }

            _lifetime = new CancellationTokenSource();
            Subscribe();
            _loop = Task.Run(() => RunLoopAsync(_lifetime.Token));
            _refreshSignal.TrySetResult(true);
            return Task.CompletedTask;
        }
    }

    public async Task<DeviceDiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ScanAndPublishAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    public async Task<DeviceDiscoverySnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        Task waitForScan;
        lock (_gate)
        {
            _pendingRefresh ??= NewSignal();
            waitForScan = _pendingRefresh.Task;
            _refreshSignal.TrySetResult(true);
        }

        await waitForScan.WaitAsync(cancellationToken).ConfigureAwait(false);
        return CurrentSnapshot;
    }

    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? lifetime;
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            lifetime = _lifetime;
            loop = _loop;
            Unsubscribe();
            lifetime?.Cancel();
            _refreshSignal.TrySetResult(true);
            _pendingRefresh?.TrySetCanceled();
            _pendingRefresh = null;
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        lifetime?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await StopAsync().ConfigureAwait(false);
            if (_ownsBolt && _bolt is not null)
            {
                await _bolt.DisposeAsync().ConfigureAwait(false);
            }

            _scanGate.Dispose();
        }
    }

    // Called with _gate held.
    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        _changeNotifier.Changed += OnHidDevicesChanged;
        _bluetooth.StatusChanged += OnLinkStateChanged;
        foreach (var source in _usbSources)
        {
            source.StateChanged += OnLinkStateChanged;
        }

        if (_bolt is not null)
        {
            _bolt.PairingChanged += OnBoltPairingChanged;
            _bolt.ReceiverClosed += OnHidDevicesChanged;
            _bolt.DeviceConnected += OnBoltLinkChanged;
            _bolt.DeviceDisconnected += OnBoltLinkChanged;
        }

        try
        {
            _changeNotifier.Start();
            _bluetooth.Start();
        }
        catch (Exception exception)
        {
            // Without events the fallback scan still finds everything.
            _log.Log(BatterioLogLevel.Warning, "Device event sources could not be started; relying on periodic scans.", exception);
        }
    }

    // Called with _gate held.
    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _subscribed = false;
        _changeNotifier.Changed -= OnHidDevicesChanged;
        _bluetooth.StatusChanged -= OnLinkStateChanged;
        foreach (var source in _usbSources)
        {
            source.StateChanged -= OnLinkStateChanged;
        }

        if (_bolt is not null)
        {
            _bolt.PairingChanged -= OnBoltPairingChanged;
            _bolt.ReceiverClosed -= OnHidDevicesChanged;
            _bolt.DeviceConnected -= OnBoltLinkChanged;
            _bolt.DeviceDisconnected -= OnBoltLinkChanged;
        }
    }

    private void OnHidDevicesChanged(object? sender, EventArgs e) => RequestDebouncedScan();

    private void OnBoltPairingChanged(object? sender, LogiBoltSlotEventArgs e) => RequestDebouncedScan();

    private void OnBoltLinkChanged(object? sender, LogiBoltSlotEventArgs e) => _ = ResolveOnlyAsync();

    private void OnLinkStateChanged(object? sender, EventArgs e) => _ = ResolveOnlyAsync();

    /// <summary>Schedules a full scan shortly; further events within the debounce window postpone it.</summary>
    private void RequestDebouncedScan()
    {
        long version;
        CancellationToken token;
        lock (_gate)
        {
            if (_stopped || _lifetime is null)
            {
                return;
            }

            version = ++_debounceVersion;
            token = _lifetime.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_options.EventDebounce, token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!_stopped && version == _debounceVersion)
                    {
                        _refreshSignal.TrySetResult(true);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    /// <summary>Re-resolves route states from the last enumeration, for link changes that need no new enumeration.</summary>
    private async Task ResolveOnlyAsync()
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_stopped || _lifetime is null)
            {
                return;
            }

            token = _lifetime.Token;
        }

        try
        {
            await _scanGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ScanData? scan;
                lock (_gate)
                {
                    scan = _lastScan;
                }

                if (scan is not null)
                {
                    Resolve(scan);
                }
            }
            finally
            {
                _scanGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Warning, "Updating device link states failed.", exception);
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Task signal;
                lock (_gate)
                {
                    signal = _refreshSignal.Task;
                }

                await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    if (ReferenceEquals(signal, _refreshSignal.Task))
                    {
                        _refreshSignal = NewSignal();
                    }
                }

                await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await ScanAndPublishAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _scanGate.Release();
                }

                CompletePendingRefresh();

                var delay = Task.Delay(_options.FallbackInterval, cancellationToken);
                lock (_gate)
                {
                    signal = _refreshSignal.Task;
                }

                var winner = await Task.WhenAny(delay, signal).ConfigureAwait(false);
                if (winner == delay)
                {
                    lock (_gate)
                    {
                        _refreshSignal.TrySetResult(true);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Error, "Device discovery loop stopped unexpectedly.", exception);
        }
    }

    private async Task<DeviceDiscoverySnapshot> ScanAndPublishAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scan = Enumerate();

        // Receivers are opened (and their devices announced) before routes are resolved, so a device is present
        // as soon as the receiver knows about it. A failed Bolt enumeration keeps the receivers as they are.
        if (_bolt is not null && !scan.Failures.ContainsKey(LogiBoltReceiverManager.BoltReceiverQuery))
        {
            try
            {
                await _bolt.SyncAsync(scan.Collections, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _log.Log(BatterioLogLevel.Warning, "Synchronising the Logi Bolt receivers failed.", exception);
            }
        }

        foreach (var source in _usbSources)
        {
            try
            {
                await source.SyncAsync(scan.Collections, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _log.Log(BatterioLogLevel.Warning, "Synchronising a USB route state source failed.", exception);
            }
        }

        lock (_gate)
        {
            _lastScan = scan;
        }

        return Resolve(scan);
    }

    /// <summary>One enumeration for every query of the catalog. If it fails, the queries are tried one by one to attribute the failure.</summary>
    private ScanData Enumerate()
    {
        var failures = new Dictionary<HidCollectionQuery, Exception>();
        if (_queries.Count == 0)
        {
            return new ScanData(Array.Empty<HidCollectionInfo>(), failures);
        }

        try
        {
            var collections = _provider.Enumerate(_queries) ?? Array.Empty<HidCollectionInfo>();
            return new ScanData(collections.ToArray(), failures);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Log(BatterioLogLevel.Trace, $"Combined HID enumeration failed ({exception.Message}); trying each query separately.");
        }

        var found = new List<HidCollectionInfo>();
        foreach (var query in _queries)
        {
            try
            {
                found.AddRange(_provider.Enumerate(query) ?? Array.Empty<HidCollectionInfo>());
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures[query] = exception;
            }
        }

        return new ScanData(found.DistinctBy(collection => collection.DevicePath, StringComparer.OrdinalIgnoreCase).ToArray(), failures);
    }

    private DeviceDiscoverySnapshot Resolve(ScanData scan)
    {
        var nextStates = new Dictionary<string, DeviceDiscoveryState>(StringComparer.OrdinalIgnoreCase);
        var routeSets = new Dictionary<string, IReadOnlyList<ResolvedRoute>>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in _catalog)
        {
            var (routes, failure) = _resolver.Resolve(definition, scan.Collections, scan.Failures);

            DeviceDiscoveryState next;
            if (routes.Count > 0)
            {
                next = new DeviceDiscoveryState
                {
                    Device = definition,
                    IsPresent = true,
                    IsOnline = routes.Any(route => route.State == RouteState.Online),
                    Error = null
                };
                routeSets[definition.Id] = routes;
            }
            else if (failure is not null)
            {
                var previous = GetState(definition.Id);
                var message = string.IsNullOrWhiteSpace(failure.Message)
                    ? failure.GetType().Name
                    : failure.Message;
                next = previous with { Error = message };
                _log.Log(BatterioLogLevel.Error,
                    $"Could not determine whether device '{definition.Id}' is present.", failure);
            }
            else
            {
                next = new DeviceDiscoveryState
                {
                    Device = definition,
                    IsPresent = false,
                    Error = null
                };
                routeSets[definition.Id] = routes;
            }

            nextStates[definition.Id] = next;
        }

        // Adapters read the routes as soon as they hear of a device, so they are published before the snapshot.
        foreach (var definition in _catalog)
        {
            if (routeSets.TryGetValue(definition.Id, out var routes))
            {
                _routeTable.SetRoutes(definition.Id, routes);
            }
        }

        DeviceDiscoverySnapshot? published = null;
        var cameOnline = new List<string>();
        lock (_gate)
        {
            var ordered = _catalog.Select(device => nextStates[device.Id]).ToArray();
            var changed = false;
            foreach (var device in _catalog)
            {
                var previous = _states[device.Id];
                var next = nextStates[device.Id];
                if (previous.IsPresent != next.IsPresent ||
                    previous.IsOnline != next.IsOnline ||
                    !string.Equals(previous.Error, next.Error, StringComparison.Ordinal))
                {
                    changed = true;
                }

                if (!previous.IsOnline && next.IsOnline)
                {
                    cameOnline.Add(device.Id);
                }
            }

            foreach (var pair in nextStates)
            {
                _states[pair.Key] = pair.Value;
            }

            if (changed)
            {
                _snapshot = new DeviceDiscoverySnapshot(++_revision, ordered);
                published = _snapshot;
            }
            else
            {
                // Return a current, ordered snapshot even when there is no event.
                _snapshot = new DeviceDiscoverySnapshot(_revision, ordered);
            }
        }

        if (published is not null)
        {
            Publish(published);
        }

        foreach (var deviceId in cameOnline)
        {
            PublishCameOnline(deviceId);
        }

        return CurrentSnapshot;
    }

    private DeviceDiscoveryState GetState(string id)
    {
        lock (_gate)
        {
            return _states[id];
        }
    }

    private DeviceDiscoveryState[] OrderedStates() =>
        _catalog.Select(device => _states[device.Id]).ToArray();

    private void CompletePendingRefresh()
    {
        TaskCompletionSource<bool>? completion;
        lock (_gate)
        {
            completion = _pendingRefresh;
            _pendingRefresh = null;
        }

        completion?.TrySetResult(true);
    }

    private void Publish(DeviceDiscoverySnapshot snapshot)
    {
        try
        {
            SnapshotChanged?.Invoke(this, new DeviceDiscoverySnapshotChangedEventArgs(snapshot));
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Error, "A discovery listener failed.", exception);
        }
    }

    private void PublishCameOnline(string deviceId)
    {
        try
        {
            DeviceCameOnline?.Invoke(this, new DeviceCameOnlineEventArgs(deviceId));
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Error, "A discovery listener failed.", exception);
        }
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record ScanData(IReadOnlyList<HidCollectionInfo> Collections, IReadOnlyDictionary<HidCollectionQuery, Exception> Failures);
}

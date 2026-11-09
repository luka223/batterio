using Batterio.Core.Abstractions;

namespace Batterio.Core;

public sealed class DeviceSelectionCoordinator : IAsyncDisposable
{
    private readonly IReadOnlyList<BuiltInDeviceDefinition> _catalog;
    private readonly IUserSettingsStore _settingsStore;
    private readonly IDeviceDiscoveryService _discovery;
    private readonly PollingCoordinator _polling;
    private readonly IBatterioLog _log;
    private readonly IDeviceRouteTable? _routes;
    private readonly RefreshCoalescer _immediateReads;
    private readonly Dictionary<string, string> _routeSets = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly HashSet<string> _sessionKnownDeviceIds = new(StringComparer.OrdinalIgnoreCase);
    private UserSettings _settings = UserSettings.Defaults();
    private DeviceDiscoverySnapshot _discoverySnapshot;
    private DeviceSelectionSnapshot _snapshot;
    private long _revision;
    private bool _started;
    private bool _stopped;
    private int _disposed;

    public DeviceSelectionCoordinator(
        IEnumerable<BuiltInDeviceDefinition> catalog,
        IUserSettingsStore settingsStore,
        IDeviceDiscoveryService discovery,
        PollingCoordinator polling,
        IBatterioLog? log = null,
        IDeviceRouteTable? routes = null,
        TimeSpan? immediateReadWindow = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _polling = polling ?? throw new ArgumentNullException(nameof(polling));
        _log = log ?? new NullBatterioLog();
        _routes = routes;
        _immediateReads = new RefreshCoalescer(
            ReadSelectedDeviceAsync,
            immediateReadWindow,
            onError: (deviceId, exception) =>
                _log.Log(BatterioLogLevel.Warning, $"Reading device '{deviceId}' after its connection changed failed.", exception));
        _catalog = catalog.Where(device => _discovery.CurrentSnapshot.Devices.Any(
            discovered => string.Equals(discovered.DeviceId, device.Id, StringComparison.OrdinalIgnoreCase))).ToArray();
        _discoverySnapshot = _discovery.CurrentSnapshot;
        _snapshot = CreateSnapshot(isLoading: true);
        _discovery.SnapshotChanged += OnDiscoverySnapshotChanged;
        _discovery.DeviceCameOnline += OnDeviceCameOnline;
        if (_routes is not null)
        {
            _routes.RoutesChanged += OnRoutesChanged;
        }
    }

    public DeviceSelectionSnapshot CurrentSnapshot
    {
        get
        {
            lock (_stateGate)
            {
                return _snapshot;
            }
        }
    }

    public event EventHandler<DeviceSelectionSnapshotChangedEventArgs>? SnapshotChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                throw new ObjectDisposedException(nameof(DeviceSelectionCoordinator));
            }

            if (_started)
            {
                return;
            }

            _settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            _started = true;
            SetLoadingSnapshot(true);
        }
        finally
        {
            _gate.Release();
        }

        await _discovery.StartAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await _discovery.RefreshAsync(cancellationToken).ConfigureAwait(false);
        await ApplyDiscoverySnapshotAsync(snapshot).ConfigureAwait(false);
    }

    public async Task RefreshDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await _discovery.RefreshAsync(cancellationToken).ConfigureAwait(false);
        await ApplyDiscoverySnapshotAsync(snapshot).ConfigureAwait(false);
    }

    public async Task SetDeviceSelectedAsync(
        string deviceId,
        bool selected,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _settings;
            var updated = current.WithDeviceSelection(deviceId, selected);
            await _settingsStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);

            // The in-memory preference is committed only after durable storage succeeds.
            _settings = updated;
            // A discovery scan may have completed while the settings file was being
            // written. Use its latest immutable result before choosing active workers.
            _discoverySnapshot = _discovery.CurrentSnapshot;
            await ReconcilePollingAsync(cancellationToken).ConfigureAwait(false);
            PublishSelectionSnapshot(CreateSnapshot(isLoading: false));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetDeviceOnTaskbarAsync(
        string deviceId,
        bool onTaskbar,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updated = _settings.WithTaskbarSelection(deviceId, onTaskbar);
            await _settingsStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);

            // The in-memory preference is committed only after durable storage succeeds.
            // Polling is unaffected by the taskbar preference, so it is not reconciled.
            _settings = updated;
            _discoverySnapshot = _discovery.CurrentSnapshot;
            PublishSelectionSnapshot(CreateSnapshot(isLoading: false));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
        }
        finally
        {
            _gate.Release();
        }

        // Discovery must stop before the polling coordinator is stopped by the app.
        await _discovery.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _discovery.SnapshotChanged -= OnDiscoverySnapshotChanged;
            _discovery.DeviceCameOnline -= OnDeviceCameOnline;
            if (_routes is not null)
            {
                _routes.RoutesChanged -= OnRoutesChanged;
            }

            _immediateReads.Dispose();
            await StopAsync().ConfigureAwait(false);
            _gate.Dispose();
        }
    }

    private void OnDiscoverySnapshotChanged(object? sender, DeviceDiscoverySnapshotChangedEventArgs args)
    {
        _ = ApplyDiscoverySnapshotAsync(args.Snapshot);
    }

    private void OnDeviceCameOnline(object? sender, DeviceCameOnlineEventArgs args)
    {
        _immediateReads.Request(args.DeviceId);
    }

    /// <summary>
    /// A device changed transport (a cable or receiver was plugged in or removed, Bluetooth appeared, a Bolt device was paired): read it
    /// now rather than at its next poll. Only a change of the routes themselves counts: a route going offline must not cause a request to
    /// a sleeping device, and one coming online is announced as <see cref="IDeviceDiscoveryService.DeviceCameOnline"/>. A device that has
    /// just appeared or vanished is skipped too: starting or stopping its polling is what reads (or stops reading) it.
    /// </summary>
    private void OnRoutesChanged(object? sender, RoutesChangedEventArgs args)
    {
        var routeSet = string.Join('|', _routes!.GetRoutes(args.DeviceId).Select(route => route.Key).Order(StringComparer.Ordinal));
        bool changedWhilePresent;
        lock (_stateGate)
        {
            _routeSets.TryGetValue(args.DeviceId, out var previous);
            _routeSets[args.DeviceId] = routeSet;
            changedWhilePresent = !string.IsNullOrEmpty(previous) && routeSet.Length > 0 && previous != routeSet;
        }

        if (changedWhilePresent)
        {
            _immediateReads.Request(args.DeviceId);
        }
    }

    /// <summary>
    /// A device woke up, was switched back to this computer or changed transport: read it now rather than at its next poll,
    /// which may be minutes away. Uses the normal refresh path, so nothing downstream changes.
    /// </summary>
    private async Task ReadSelectedDeviceAsync(string deviceId)
    {
        try
        {
            // Wait for the discovery snapshot that announced the device to be applied first: that is what starts its polling.
            await _gate.WaitAsync().ConfigureAwait(false);
            bool selected;
            try
            {
                selected = _started && !_stopped && _settings.IsDeviceSelected(deviceId) &&
                           _discoverySnapshot.PresentDeviceIds.Contains(deviceId);
            }
            finally
            {
                _gate.Release();
            }

            if (selected)
            {
                await _polling.RefreshDeviceAsync(deviceId).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is ObjectDisposedException or OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task ApplyDiscoverySnapshotAsync(DeviceDiscoverySnapshot discoverySnapshot)
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_stopped || !_started)
                {
                    return;
                }

                _discoverySnapshot = discoverySnapshot;
                await RememberPresentDevicesAsync().ConfigureAwait(false);
                await ReconcilePollingAsync().ConfigureAwait(false);
                PublishSelectionSnapshot(CreateSnapshot(isLoading: false));
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Error, "Applying a device discovery result failed.", exception);
        }
    }

    private async Task RememberPresentDevicesAsync()
    {
        var newlySeen = _discoverySnapshot.Devices
            .Where(device => device.IsPresent && !_settings.IsDeviceKnown(device.DeviceId))
            .Select(device => device.DeviceId)
            .ToArray();
        if (newlySeen.Length == 0)
        {
            return;
        }

        // Keep the device listed for this session even if persisting it fails;
        // the next discovery pass retries the save because it is still unsaved.
        var updated = _settings.WithKnownDevices(newlySeen);
        try
        {
            await _settingsStore.SaveAsync(updated).ConfigureAwait(false);
            _settings = updated;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Log(BatterioLogLevel.Warning, "Could not remember newly connected devices.", exception);
            _sessionKnownDeviceIds.UnionWith(newlySeen);
        }
    }

    private bool IsListed(DeviceDiscoveryState device) =>
        device.IsPresent ||
        _settings.IsDeviceKnown(device.DeviceId) ||
        _sessionKnownDeviceIds.Contains(device.DeviceId);

    private async Task ReconcilePollingAsync(CancellationToken cancellationToken = default)
    {
        var active = _discoverySnapshot.Devices
            .Where(device => device.IsPresent && _settings.IsDeviceSelected(device.DeviceId))
            .Select(device => device.Device.CreateConfiguration())
            .ToArray();
        await _polling.SetActiveDevicesAsync(active, cancellationToken).ConfigureAwait(false);
    }

    private DeviceSelectionSnapshot CreateSnapshot(bool isLoading)
    {
        var devices = _discoverySnapshot.Devices
            .Where(IsListed)
            .Select(device => new DeviceSelectionState
            {
                Device = device.Device,
                IsPresent = device.IsPresent,
                IsSelected = _settings.IsDeviceSelected(device.DeviceId),
                IsOnTaskbar = _settings.IsDeviceOnTaskbar(device.DeviceId),
                Error = device.Error
            })
            .ToArray();
        return new DeviceSelectionSnapshot(++_revision, isLoading, devices);
    }

    private void SetLoadingSnapshot(bool isLoading)
    {
        DeviceSelectionSnapshot snapshot;
        lock (_stateGate)
        {
            snapshot = CreateSnapshot(isLoading);
            _snapshot = snapshot;
        }

        Publish(snapshot);
    }

    private void PublishSelectionSnapshot(DeviceSelectionSnapshot snapshot)
    {
        lock (_stateGate)
        {
            _snapshot = snapshot;
        }

        Publish(snapshot);
    }

    private void Publish(DeviceSelectionSnapshot snapshot)
    {
        try
        {
            SnapshotChanged?.Invoke(this, new DeviceSelectionSnapshotChangedEventArgs(snapshot));
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Error, "A device selection listener failed.", exception);
        }
    }
}

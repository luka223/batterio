using Batterio.Core.Abstractions;

namespace Batterio.Core;

public sealed class DeviceSelectionCoordinator : IAsyncDisposable
{
    private readonly IReadOnlyList<BuiltInDeviceDefinition> _catalog;
    private readonly IUserSettingsStore _settingsStore;
    private readonly IDeviceDiscoveryService _discovery;
    private readonly PollingCoordinator _polling;
    private readonly IBatterioLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
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
        IBatterioLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _polling = polling ?? throw new ArgumentNullException(nameof(polling));
        _log = log ?? new NullBatterioLog();
        _catalog = catalog.Where(device => _discovery.CurrentSnapshot.Devices.Any(
            discovered => string.Equals(discovered.DeviceId, device.Id, StringComparison.OrdinalIgnoreCase))).ToArray();
        _discoverySnapshot = _discovery.CurrentSnapshot;
        _snapshot = CreateSnapshot(isLoading: true);
        _discovery.SnapshotChanged += OnDiscoverySnapshotChanged;
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
            await StopAsync().ConfigureAwait(false);
            _gate.Dispose();
        }
    }

    private void OnDiscoverySnapshotChanged(object? sender, DeviceDiscoverySnapshotChangedEventArgs args)
    {
        _ = ApplyDiscoverySnapshotAsync(args.Snapshot);
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
            .Select(device => new DeviceSelectionState
            {
                Device = device.Device,
                IsPresent = device.IsPresent,
                IsSelected = _settings.IsDeviceSelected(device.DeviceId),
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

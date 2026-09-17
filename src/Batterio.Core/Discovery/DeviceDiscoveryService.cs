using Batterio.Core.Abstractions;

namespace Batterio.Core;

public sealed class DeviceDiscoveryService : IDeviceDiscoveryService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<BuiltInDeviceDefinition> _catalog;
    private readonly AdapterRegistry _registry;
    private readonly IHidCollectionProvider _provider;
    private readonly IBatterioLog _log;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceDiscoveryState> _states;
    private CancellationTokenSource? _lifetime;
    private Task? _loop;
    private TaskCompletionSource<bool> _refreshSignal = NewSignal();
    private TaskCompletionSource<bool>? _pendingRefresh;
    private DeviceDiscoverySnapshot _snapshot;
    private long _revision;
    private bool _stopped;
    private int _disposed;

    public DeviceDiscoveryService(
        IEnumerable<BuiltInDeviceDefinition> catalog,
        AdapterRegistry registry,
        IHidCollectionProvider provider,
        IBatterioLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _log = log ?? new NullBatterioLog();

        _catalog = catalog
            .Where(device => _registry.TryGetFactory(device.AdapterId, out _))
            .ToArray();
        _states = _catalog.ToDictionary(
            device => device.Id,
            device => new DeviceDiscoveryState { Device = device },
            StringComparer.OrdinalIgnoreCase);
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
            _scanGate.Dispose();
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

                var delay = Task.Delay(ScanInterval, cancellationToken);
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
        var nextStates = new Dictionary<string, DeviceDiscoveryState>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in _catalog)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeviceDiscoveryState next;
            Exception? enumerationFailure = null;
            var isPresent = false;
            foreach (var query in definition.PresenceQueries)
            {
                try
                {
                    var collections = _provider.Enumerate(query) ?? Array.Empty<HidCollectionInfo>();
                    if (collections.Any(query.Matches))
                    {
                        isPresent = true;
                        break;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    enumerationFailure ??= exception;
                }
            }

            if (isPresent)
            {
                next = new DeviceDiscoveryState
                {
                    Device = definition,
                    IsPresent = true,
                    Error = null
                };
            }
            else if (enumerationFailure is not null)
            {
                var previous = GetState(definition.Id);
                var message = string.IsNullOrWhiteSpace(enumerationFailure.Message)
                    ? enumerationFailure.GetType().Name
                    : enumerationFailure.Message;
                next = previous with { Error = message };
                _log.Log(BatterioLogLevel.Error,
                    $"Could not determine whether device '{definition.Id}' is present.", enumerationFailure);
            }
            else
            {
                next = new DeviceDiscoveryState
                {
                    Device = definition,
                    IsPresent = false,
                    Error = null
                };
            }

            nextStates[definition.Id] = next;
        }

        DeviceDiscoverySnapshot? published = null;
        lock (_gate)
        {
            var ordered = _catalog.Select(device => nextStates[device.Id]).ToArray();
            var changed = _catalog.Any(device =>
            {
                var previous = _states[device.Id];
                var next = nextStates[device.Id];
                return previous.IsPresent != next.IsPresent ||
                       !string.Equals(previous.Error, next.Error, StringComparison.Ordinal);
            });

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

        await Task.CompletedTask;
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

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

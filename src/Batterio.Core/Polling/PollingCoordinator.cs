using System.Text.Json;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

public sealed class PollingCoordinator : IAsyncDisposable
{
    private readonly AdapterRegistry _registry;
    private readonly IBatterioLog _log;
    private readonly SemaphoreSlim _globalConcurrency = new(4, 4);
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceWorker> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceBatteryState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceConfiguration> _desired = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _stateOrder = new();
    private readonly Dictionary<string, Task> _retirements = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _lifetime = new();
    private long _generation;
    private long _workerIdentity;
    private long _snapshotRevision;
    private PollingSnapshot _snapshot = new(0, Array.Empty<DeviceBatteryState>());
    private bool _stopped;

    public PollingCoordinator(AdapterRegistry registry, IBatterioLog? log = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _log = log ?? new NullBatterioLog();
    }

    public event EventHandler<DeviceBatteryStateChangedEventArgs>? StateChanged;

    public event EventHandler<PollingSnapshotChangedEventArgs>? SnapshotChanged;

    public IReadOnlyList<DeviceBatteryState> States => CurrentSnapshot.States;

    public PollingSnapshot CurrentSnapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public Task ApplyConfigurationAsync(ConfigurationSnapshot snapshot, CancellationToken cancellationToken = default) =>
        StartDevicesAsync(snapshot.Devices, cancellationToken);

    public async Task StartDevicesAsync(IReadOnlyList<DeviceConfiguration> devices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(devices);
        await _reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReplaceDevicesAsync(devices, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    public async Task SetActiveDevicesAsync(IReadOnlyList<DeviceConfiguration> devices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(devices);
        await _reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReconcileDevicesAsync(devices, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    public async Task RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        DeviceWorker[] workers;
        lock (_gate)
        {
            ThrowIfStopped();
            workers = _workers.Values.ToArray();
        }

        await Task.WhenAll(workers.Select(worker => worker.RequestRefreshAsync(cancellationToken))).ConfigureAwait(false);
    }

    public async Task RefreshDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        DeviceWorker? worker;
        lock (_gate)
        {
            ThrowIfStopped();
            _workers.TryGetValue(deviceId, out worker);
        }

        if (worker is not null)
        {
            await worker.RequestRefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(TimeSpan? drainTimeout = null)
    {
        DeviceWorker[] workers;
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            workers = _workers.Values.ToArray();
            _workers.Clear();
            _desired.Clear();
            _lifetime.Cancel();
        }

        foreach (var worker in workers)
        {
            worker.Stop();
            ScheduleRetirement(worker);
        }

        Task allRetirements;
        lock (_gate)
        {
            allRetirements = Task.WhenAll(_retirements.Values.ToArray());
        }

        if (drainTimeout is { } timeout)
        {
            await Task.WhenAny(allRetirements, Task.Delay(timeout)).ConfigureAwait(false);
        }
        else
        {
            await allRetirements.ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _globalConcurrency.Dispose();
        _reconcileGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task ReplaceDevicesAsync(IReadOnlyList<DeviceConfiguration> devices, CancellationToken cancellationToken)
    {
        var desired = NormalizeDevices(devices);
        DeviceWorker[] previousWorkers;
        PollingSnapshot snapshot;
        long generation;
        lock (_gate)
        {
            ThrowIfStopped();
            generation = ++_generation;
            previousWorkers = _workers.Values.ToArray();
            _workers.Clear();
            _desired.Clear();
            foreach (var device in desired)
            {
                _desired[device.Id] = device;
            }

            _states.Clear();
            _stateOrder.Clear();
            foreach (var device in desired)
            {
                _stateOrder.Add(device.Id);
                _states[device.Id] = InitialState(device);
            }

            snapshot = PublishSnapshotLocked();
        }

        Publish(snapshot, snapshot.States);

        foreach (var worker in previousWorkers)
        {
            worker.Stop();
            ScheduleRetirement(worker);
        }

        await CreateWorkersAsync(desired, generation, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileDevicesAsync(IReadOnlyList<DeviceConfiguration> devices, CancellationToken cancellationToken)
    {
        var desired = NormalizeDevices(devices);
        var retired = new List<DeviceWorker>();
        var toCreate = new List<DeviceConfiguration>();
        PollingSnapshot? membershipSnapshot = null;

        lock (_gate)
        {
            ThrowIfStopped();
            var desiredById = desired.ToDictionary(device => device.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _workers.ToArray())
            {
                if (!desiredById.TryGetValue(pair.Key, out var next) || !ConfigurationsEqual(pair.Value.Device, next))
                {
                    _workers.Remove(pair.Key);
                    retired.Add(pair.Value);
                }
            }

            _desired.Clear();
            foreach (var device in desired)
            {
                _desired[device.Id] = device;
            }

            var oldStates = _stateOrder.Select(id => _states[id]).ToArray();
            _stateOrder.Clear();
            foreach (var device in desired)
            {
                _stateOrder.Add(device.Id);
                if (!_states.TryGetValue(device.Id, out var oldState) ||
                    !ConfigurationsEqual(oldState.Configuration, device))
                {
                    _states[device.Id] = InitialState(device);
                }
            }

            foreach (var id in _states.Keys.Where(id => !desiredById.ContainsKey(id)).ToArray())
            {
                _states.Remove(id);
            }

            var newStates = _stateOrder.Select(id => _states[id]).ToArray();
            if (!StateListsEqual(oldStates, newStates) ||
                retired.Count > 0 ||
                desired.Any(device => device.Enabled && !_workers.ContainsKey(device.Id)))
            {
                membershipSnapshot = PublishSnapshotLocked();
            }

            toCreate.AddRange(desired.Where(device => device.Enabled && !_workers.ContainsKey(device.Id)));
        }

        if (membershipSnapshot is not null)
        {
            Publish(membershipSnapshot, membershipSnapshot.States);
        }

        foreach (var worker in retired)
        {
            worker.Stop();
            ScheduleRetirement(worker);
        }

        await CreateWorkersAsync(toCreate, _generation, cancellationToken).ConfigureAwait(false);
    }

    private async Task CreateWorkersAsync(
        IEnumerable<DeviceConfiguration> devices,
        long generation,
        CancellationToken cancellationToken)
    {
        using var operationLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var operationToken = operationLifetime.Token;
        foreach (var device in devices)
        {
            operationToken.ThrowIfCancellationRequested();
            await WaitForRetirementAsync(device.Id, operationToken).ConfigureAwait(false);

            lock (_gate)
            {
                if (_stopped || generation != _generation ||
                    !_desired.TryGetValue(device.Id, out var desired) ||
                    !ConfigurationsEqual(desired, device) ||
                    _workers.ContainsKey(device.Id))
                {
                    continue;
                }
            }

            try
            {
                var adapter = _registry.CreateAdapter(device);
                var worker = new DeviceWorker(this, device, adapter, generation, Interlocked.Increment(ref _workerIdentity), _lifetime.Token);
                var added = false;
                lock (_gate)
                {
                    if (!_stopped && generation == _generation &&
                        _desired.TryGetValue(device.Id, out var desired) &&
                        ConfigurationsEqual(desired, device) && !_workers.ContainsKey(device.Id))
                    {
                        _workers[device.Id] = worker;
                        added = true;
                    }
                }

                if (added)
                {
                    worker.Start();
                }
                else
                {
                    worker.Stop();
                    await worker.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                _log.Log(BatterioLogLevel.Error,
                    $"Adapter configuration failed for device '{device.Id}' (adapter '{device.AdapterId}').",
                    exception);
                SetError(device.Id, device, exception.Message);
            }
        }
    }

    private async Task WaitForRetirementAsync(string deviceId, CancellationToken cancellationToken)
    {
        Task? retirement;
        lock (_gate)
        {
            _retirements.TryGetValue(deviceId, out retirement);
        }

        if (retirement is not null)
        {
            await retirement.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void ScheduleRetirement(DeviceWorker worker)
    {
        var retirement = RetireWorkerAsync(worker);
        lock (_gate)
        {
            _retirements[worker.Device.Id] = retirement;
        }

        _ = retirement.ContinueWith(
            _ =>
            {
                lock (_gate)
                {
                    if (_retirements.TryGetValue(worker.Device.Id, out var current) && ReferenceEquals(current, retirement))
                    {
                        _retirements.Remove(worker.Device.Id);
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RetireWorkerAsync(DeviceWorker worker)
    {
        try
        {
            await worker.Completion.ConfigureAwait(false);
            await worker.DisposeAdapterAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Error, $"Retired adapter for device '{worker.Device.Id}' failed to stop.", exception);
        }
    }

    private async Task RunAttemptAsync(DeviceWorker worker)
    {
        var acquired = false;
        try
        {
            await _globalConcurrency.WaitAsync(worker.Token).ConfigureAwait(false);
            acquired = true;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(worker.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(worker.Device.TimeoutSeconds));
            var adapterTask = worker.Adapter.GetBatteryStatus(timeout.Token);
            var completed = await Task.WhenAny(
                adapterTask,
                Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token)).ConfigureAwait(false);

            if (completed != adapterTask)
            {
                if (worker.Token.IsCancellationRequested)
                {
                    await AwaitAdapterAndReleaseAsync(adapterTask, acquired).ConfigureAwait(false);
                    acquired = false;
                    return;
                }

                UpdateFailure(worker, "The adapter timed out.");
                _log.Log(BatterioLogLevel.Warning,
                    $"Adapter timed out for device '{worker.Device.Id}' (adapter '{worker.Device.AdapterId}').");
                await AwaitAdapterAndReleaseAsync(adapterTask, acquired).ConfigureAwait(false);
                acquired = false;
                return;
            }

            BatteryReading reading;
            try
            {
                reading = await adapterTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!worker.Token.IsCancellationRequested)
            {
                UpdateFailure(worker, "The adapter timed out.");
                _log.Log(BatterioLogLevel.Warning,
                    $"Adapter timed out for device '{worker.Device.Id}' (adapter '{worker.Device.AdapterId}').");
                return;
            }

            if (reading is null)
            {
                UpdateFailure(worker, "The adapter returned no battery reading.");
                _log.Log(BatterioLogLevel.Error,
                    $"Adapter returned no battery reading for device '{worker.Device.Id}' (adapter '{worker.Device.AdapterId}').");
                return;
            }

            UpdateState(worker, state => state with
            {
                Reading = reading,
                Error = null
            });
        }
        catch (OperationCanceledException) when (worker.Token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            UpdateFailure(worker, exception.Message);
            _log.Log(BatterioLogLevel.Error,
                $"Adapter failed for device '{worker.Device.Id}' (adapter '{worker.Device.AdapterId}').",
                exception);
        }
        finally
        {
            if (acquired)
            {
                _globalConcurrency.Release();
            }
        }
    }

    private async Task AwaitAdapterAndReleaseAsync(Task<BatteryReading> adapterTask, bool acquired)
    {
        try
        {
            await adapterTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Error, "An adapter completed after timing out.", exception);
        }
        finally
        {
            if (acquired)
            {
                _globalConcurrency.Release();
            }
        }
    }

    private void UpdateFailure(DeviceWorker worker, string error) =>
        UpdateState(worker, state => state with
        {
            Reading = BatteryReading.Offline,
            Error = error
        });

    private void UpdateState(DeviceWorker worker, Func<DeviceBatteryState, DeviceBatteryState> update)
    {
        PollingSnapshot? snapshot = null;
        DeviceBatteryState? state = null;
        lock (_gate)
        {
            if (!_workers.TryGetValue(worker.Device.Id, out var active) ||
                !ReferenceEquals(active, worker) ||
                !_states.TryGetValue(worker.Device.Id, out var current))
            {
                return;
            }

            var next = update(current);
            if (StateEquivalent(current, next))
            {
                state = current;
            }
            else
            {
                _states[worker.Device.Id] = next;
                snapshot = PublishSnapshotLocked();
                state = next;
            }
        }

        if (snapshot is not null)
        {
            Publish(snapshot, new[] { state });
        }
        else
        {
            PublishStateChanged(state);
        }
    }

    private void SetError(string deviceId, DeviceConfiguration configuration, string error)
    {
        PollingSnapshot? snapshot = null;
        lock (_gate)
        {
            if (!_states.TryGetValue(deviceId, out var current) ||
                !ConfigurationsEqual(current.Configuration, configuration))
            {
                return;
            }

            var next = current with { Reading = BatteryReading.Offline, Error = error };
            if (!StateEquivalent(current, next))
            {
                _states[deviceId] = next;
                snapshot = PublishSnapshotLocked();
            }
        }

        if (snapshot is not null)
        {
            Publish(snapshot, snapshot.States.Where(state =>
                string.Equals(state.Configuration.Id, deviceId, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private PollingSnapshot PublishSnapshotLocked()
    {
        _snapshot = new PollingSnapshot(
            ++_snapshotRevision,
            _stateOrder.Where(_states.ContainsKey).Select(id => _states[id]).ToArray());
        return _snapshot;
    }

    private void Publish(PollingSnapshot snapshot, IEnumerable<DeviceBatteryState> compatibilityStates)
    {
        foreach (var state in compatibilityStates)
        {
            PublishStateChanged(state);
        }

        try
        {
            SnapshotChanged?.Invoke(this, new PollingSnapshotChangedEventArgs(snapshot));
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Error, "A polling snapshot listener failed.", exception);
        }
    }

    private void PublishStateChanged(DeviceBatteryState state)
    {
        try
        {
            StateChanged?.Invoke(this, new DeviceBatteryStateChangedEventArgs(state));
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Error, "A state listener failed.", exception);
        }
    }

    private void ThrowIfStopped()
    {
        if (_stopped)
        {
            throw new ObjectDisposedException(nameof(PollingCoordinator));
        }
    }

    private static DeviceBatteryState InitialState(DeviceConfiguration device) => new()
    {
        Configuration = device,
        Reading = BatteryReading.Offline
    };

    private static IReadOnlyList<DeviceConfiguration> NormalizeDevices(IReadOnlyList<DeviceConfiguration> devices)
    {
        var result = new List<DeviceConfiguration>(devices.Count);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            ArgumentNullException.ThrowIfNull(device);
            if (string.IsNullOrWhiteSpace(device.Id))
            {
                throw new ArgumentException("Every active device must have a nonempty id.", nameof(devices));
            }

            if (!ids.Add(device.Id))
            {
                throw new ArgumentException($"Device id '{device.Id}' is duplicated.", nameof(devices));
            }

            result.Add(device);
        }

        return result;
    }

    private static bool StateListsEqual(IReadOnlyList<DeviceBatteryState> first, IReadOnlyList<DeviceBatteryState> second) =>
        first.Count == second.Count && first.Zip(second).All(pair =>
            string.Equals(pair.First.Configuration.Id, pair.Second.Configuration.Id, StringComparison.OrdinalIgnoreCase) &&
            StateEquivalent(pair.First, pair.Second));

    private static bool StateEquivalent(DeviceBatteryState first, DeviceBatteryState second) =>
        ConfigurationsEqual(first.Configuration, second.Configuration) &&
        Equals(first.Reading, second.Reading) &&
        string.Equals(first.Error, second.Error, StringComparison.Ordinal);

    private static bool ConfigurationsEqual(DeviceConfiguration first, DeviceConfiguration second) =>
        string.Equals(first.Id, second.Id, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(first.Name, second.Name, StringComparison.Ordinal) &&
        string.Equals(first.PhotoPath, second.PhotoPath, StringComparison.Ordinal) &&
        string.Equals(first.AdapterId, second.AdapterId, StringComparison.OrdinalIgnoreCase) &&
        first.Enabled == second.Enabled &&
        first.PollIntervalSeconds == second.PollIntervalSeconds &&
        first.TimeoutSeconds == second.TimeoutSeconds &&
        JsonElementsEqual(first.AdapterSettings, second.AdapterSettings);

    private static bool JsonElementsEqual(JsonElement first, JsonElement second)
    {
        if (first.ValueKind == JsonValueKind.Undefined || second.ValueKind == JsonValueKind.Undefined)
        {
            return first.ValueKind == second.ValueKind;
        }

        return JsonElement.DeepEquals(first, second);
    }

    private sealed class DeviceWorker : IAsyncDisposable
    {
        private readonly PollingCoordinator _owner;
        private readonly CancellationTokenSource _cancellation;
        private TaskCompletionSource<bool> _refreshSignal = NewSignal();
        private readonly object _gate = new();
        private Task? _loop;
        private TaskCompletionSource<bool>? _refreshCompletion;
        private bool _stopped;
        private int _adapterDisposed;

        public DeviceWorker(
            PollingCoordinator owner,
            DeviceConfiguration device,
            IBatteryAdapter adapter,
            long generation,
            long identity,
            CancellationToken lifetimeToken)
        {
            _owner = owner;
            Device = device;
            Adapter = adapter;
            Generation = generation;
            Identity = identity;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        }

        public DeviceConfiguration Device { get; }

        public IBatteryAdapter Adapter { get; }

        public long Generation { get; }

        public long Identity { get; }

        public CancellationToken Token => _cancellation.Token;

        public Task Completion => _loop ?? Task.CompletedTask;

        public void Start() => _loop = Task.Run(RunAsync);

        public void Stop()
        {
            lock (_gate)
            {
                if (_stopped)
                {
                    return;
                }

                _stopped = true;
                _cancellation.Cancel();
                _refreshSignal.TrySetResult(true);
                _refreshCompletion?.TrySetCanceled();
                _refreshCompletion = null;
            }
        }

        public Task RequestRefreshAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_stopped)
                {
                    return Task.CompletedTask;
                }

                _refreshCompletion ??= NewSignal();
                _refreshSignal.TrySetResult(true);
                return _refreshCompletion.Task.WaitAsync(cancellationToken);
            }
        }

        public async Task DisposeAdapterAsync()
        {
            if (Interlocked.Exchange(ref _adapterDisposed, 1) == 0)
            {
                await Adapter.DisposeAsync().ConfigureAwait(false);
                _cancellation.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            Stop();
            await Completion.ConfigureAwait(false);
            await DisposeAdapterAsync().ConfigureAwait(false);
        }

        private async Task RunAsync()
        {
            try
            {
                var first = true;
                while (!Token.IsCancellationRequested)
                {
                    var refreshTriggered = false;
                    if (!first)
                    {
                        var interval = Task.Delay(TimeSpan.FromSeconds(Device.PollIntervalSeconds), Token);
                        Task refresh;
                        lock (_gate)
                        {
                            refresh = _refreshSignal.Task;
                        }

                        var winner = await Task.WhenAny(interval, refresh).ConfigureAwait(false);
                        if (winner == interval)
                        {
                            await interval.ConfigureAwait(false);
                        }
                        else
                        {
                            await refresh.ConfigureAwait(false);
                            lock (_gate)
                            {
                                if (ReferenceEquals(refresh, _refreshSignal.Task))
                                {
                                    _refreshSignal = NewSignal();
                                }
                            }

                            refreshTriggered = true;
                        }
                    }

                    first = false;
                    Task attempt;
                    lock (_gate)
                    {
                        if (_stopped)
                        {
                            break;
                        }

                        attempt = _owner.RunAttemptAsync(this);
                    }

                    try
                    {
                        await attempt.ConfigureAwait(false);
                    }
                    finally
                    {
                        if (refreshTriggered)
                        {
                            lock (_gate)
                            {
                                _refreshCompletion?.TrySetResult(true);
                                _refreshCompletion = null;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _owner._log.Log(BatterioLogLevel.Error, $"Polling stopped for device '{Device.Id}'.", exception);
            }
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

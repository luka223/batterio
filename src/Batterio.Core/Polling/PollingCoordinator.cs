using Batterio.Core.Abstractions;

namespace Batterio.Core;

public sealed class PollingCoordinator : IAsyncDisposable
{
    private readonly AdapterRegistry _registry;
    private readonly IClock _clock;
    private readonly IBatterioLog _log;
    private readonly SemaphoreSlim _globalConcurrency = new(4, 4);
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceWorker> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceBatteryState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _stateOrder = new();
    private readonly List<Task> _retiredWorkers = new();
    private CancellationTokenSource _lifetime = new();
    private long _generation;
    private bool _stopped;

    public PollingCoordinator(AdapterRegistry registry, IClock? clock = null, IBatterioLog? log = null)
    {
        _registry = registry;
        _clock = clock ?? new SystemClock();
        _log = log ?? new NullBatterioLog();
    }

    public event EventHandler<DeviceBatteryStateChangedEventArgs>? StateChanged;

    public IReadOnlyList<DeviceBatteryState> States
    {
        get
        {
            lock (_gate)
            {
                return _stateOrder
                    .Where(_states.ContainsKey)
                    .Select(id => _states[id])
                    .ToArray();
            }
        }
    }

    public Task ApplyConfigurationAsync(ConfigurationSnapshot snapshot, CancellationToken cancellationToken = default) =>
        StartDevicesAsync(snapshot.Devices, cancellationToken);

    public async Task StartDevicesAsync(IReadOnlyList<DeviceConfiguration> devices, CancellationToken cancellationToken = default)
    {
        DeviceWorker[] previousWorkers;
        long generation;
        lock (_gate)
        {
            ThrowIfStopped();
            generation = ++_generation;
            previousWorkers = _workers.Values.ToArray();
            _workers.Clear();
            _states.Clear();
            _stateOrder.Clear();
            foreach (var device in devices)
            {
                _stateOrder.Add(device.Id);
                _states[device.Id] = new DeviceBatteryState
                {
                    Configuration = device,
                    RetrievalState = device.Enabled
                        ? BatteryRetrievalState.Loading
                        : BatteryRetrievalState.Disabled
                };
            }
        }

        foreach (var state in States)
        {
            Publish(state);
        }

        foreach (var worker in previousWorkers)
        {
            worker.Stop();
            var retirement = RetireWorkerAsync(worker);
            lock (_gate)
            {
                _retiredWorkers.Add(retirement);
            }
        }

        var newWorkers = new List<DeviceWorker>();
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!device.Enabled)
            {
                continue;
            }

            try
            {
                var adapter = _registry.CreateAdapter(device);
                var worker = new DeviceWorker(this, device, adapter, generation, _lifetime.Token);
                lock (_gate)
                {
                    if (_stopped || generation != _generation)
                    {
                        worker.Stop();
                    }
                    else
                    {
                        _workers[device.Id] = worker;
                        newWorkers.Add(worker);
                    }
                }
            }
            catch (Exception exception)
            {
                _log.Log(BatterioLogLevel.Error,
                    $"Adapter configuration failed for device '{device.Id}' (adapter '{device.AdapterId}').",
                    exception);
                UpdateState(device.Id, generation, state => state with
                {
                    RetrievalState = BatteryRetrievalState.ConfigurationError,
                    Error = exception.Message
                });
            }
        }

        foreach (var worker in newWorkers)
        {
            worker.Start();
        }

        await Task.CompletedTask;
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
            _lifetime.Cancel();
        }

        foreach (var worker in workers)
        {
            worker.Stop();
        }

        var retirementTasks = workers.Select(RetireWorkerAsync).ToArray();
        var allRetirements = Task.WhenAll(retirementTasks);
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
        _lifetime.Dispose();
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
        UpdateState(worker.Device.Id, worker.Generation, state => state with
        {
            RetrievalState = BatteryRetrievalState.Retrieving,
            Error = null
        });

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
                UpdateFailure(worker, "The adapter timed out.");
                _log.Log(BatterioLogLevel.Warning,
                    $"Adapter timed out for device '{worker.Device.Id}' (adapter '{worker.Device.AdapterId}').");
                await AwaitAdapterAndReleaseAsync(adapterTask, acquired).ConfigureAwait(false);
                acquired = false;
                return;
            }

            int percentage;
            try
            {
                percentage = await adapterTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!worker.Token.IsCancellationRequested)
            {
                UpdateFailure(worker, "The adapter timed out.");
                _log.Log(BatterioLogLevel.Warning,
                    $"Adapter timed out for device '{worker.Device.Id}' (adapter '{worker.Device.AdapterId}').");
                return;
            }

            if (percentage is < 0 or > 100)
            {
                UpdateFailure(worker, $"The adapter returned an invalid percentage ({percentage}).");
                _log.Log(BatterioLogLevel.Error,
                    $"Adapter returned invalid percentage for device '{worker.Device.Id}' (adapter '{worker.Device.AdapterId}').");
                return;
            }

            UpdateState(worker.Device.Id, worker.Generation, state => state with
            {
                LastValidPercentage = percentage,
                RetrievalState = BatteryRetrievalState.Ready,
                LastSuccessfulUpdate = _clock.UtcNow,
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

    private async Task AwaitAdapterAndReleaseAsync(Task<int> adapterTask, bool acquired)
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

    private void UpdateFailure(DeviceWorker worker, string error)
    {
        UpdateState(worker.Device.Id, worker.Generation, state => state with
        {
            RetrievalState = state.LastValidPercentage.HasValue
                ? BatteryRetrievalState.Stale
                : BatteryRetrievalState.Unavailable,
            Error = error
        });
    }

    private void UpdateState(string deviceId, long generation, Func<DeviceBatteryState, DeviceBatteryState> update)
    {
        DeviceBatteryState? next = null;
        lock (_gate)
        {
            if (generation != _generation || !_states.TryGetValue(deviceId, out var current))
            {
                return;
            }

            next = update(current);
            _states[deviceId] = next;
        }

        Publish(next);
    }

    private void Publish(DeviceBatteryState state)
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

    private sealed class DeviceWorker : IAsyncDisposable
    {
        private readonly PollingCoordinator _owner;
        private readonly CancellationTokenSource _cancellation;
        private TaskCompletionSource<bool> _refreshSignal = NewSignal();
        private readonly object _gate = new();
        private Task? _loop;
        private Task? _activeAttempt;
        private TaskCompletionSource<bool>? _refreshCompletion;
        private bool _stopped;

        public DeviceWorker(PollingCoordinator owner, DeviceConfiguration device, IBatteryAdapter adapter, long generation, CancellationToken lifetimeToken)
        {
            _owner = owner;
            Device = device;
            Adapter = adapter;
            Generation = generation;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        }

        public DeviceConfiguration Device { get; }

        public IBatteryAdapter Adapter { get; }

        public long Generation { get; }

        public CancellationToken Token => _cancellation.Token;

        public Task Completion => _loop ?? Task.CompletedTask;

        public void Start()
        {
            _loop = Task.Run(RunAsync);
        }

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

                _refreshCompletion ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _refreshSignal.TrySetResult(true);

                return _refreshCompletion.Task.WaitAsync(cancellationToken);
            }
        }

        public async Task DisposeAdapterAsync()
        {
            await Adapter.DisposeAsync().ConfigureAwait(false);
            _cancellation.Dispose();
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
                        _activeAttempt = attempt;
                    }

                    try
                    {
                        await attempt.ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            if (ReferenceEquals(_activeAttempt, attempt))
                            {
                                _activeAttempt = null;
                            }

                            if (refreshTriggered)
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

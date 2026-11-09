namespace Batterio.Core;

/// <summary>
/// Turns bursts of "read this device now" triggers into single refreshes. A receiver plug-in raises several events, a Bluetooth link can
/// flap, and one change can announce itself twice (its routes changed and the device came online); each real change should cost one read.
/// </summary>
/// <remarks>
/// The first trigger for a device starts a short window; every trigger inside it is absorbed, and when it ends the device is refreshed
/// once. A trigger that arrives while that refresh is running is not queued as a second read right away: at most one follow-up runs
/// after the running one finished. Devices are independent of each other.
/// </remarks>
internal sealed class RefreshCoalescer : IDisposable
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(1);

    private readonly Func<string, Task> _refresh;
    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string, Exception>? _onError;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceState> _devices = new(StringComparer.OrdinalIgnoreCase);

    public RefreshCoalescer(
        Func<string, Task> refresh,
        TimeSpan? window = null,
        TimeProvider? timeProvider = null,
        Action<string, Exception>? onError = null)
    {
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _window = window ?? DefaultWindow;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _onError = onError;
    }

    /// <summary>Asks for <paramref name="deviceId"/> to be refreshed soon. Cheap and non-blocking.</summary>
    public void Request(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        DeviceState state;
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested)
            {
                return;
            }

            if (!_devices.TryGetValue(deviceId, out state!))
            {
                _devices[deviceId] = state = new DeviceState();
            }

            if (state.Scheduled)
            {
                return;
            }

            if (state.Running)
            {
                state.FollowUp = true;
                return;
            }

            state.Scheduled = true;
        }

        _ = RunAsync(deviceId, state);
    }

    private async Task RunAsync(string deviceId, DeviceState state)
    {
        var token = _lifetime.Token;
        try
        {
            var again = true;
            while (again)
            {
                await Task.Delay(_window, _timeProvider, token).ConfigureAwait(false);
                lock (_gate)
                {
                    state.Scheduled = false;
                    state.Running = true;
                    state.FollowUp = false;
                }

                try
                {
                    await _refresh(deviceId).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _onError?.Invoke(deviceId, exception);
                }

                lock (_gate)
                {
                    state.Running = false;
                    again = state.FollowUp;
                    state.FollowUp = false;
                    state.Scheduled = again;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested)
            {
                return;
            }

            _lifetime.Cancel();
        }
    }

    private sealed class DeviceState
    {
        public bool Scheduled;
        public bool Running;
        public bool FollowUp;
    }
}

using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Base of the monitors that learn a device's state from frames a receiver or hub sends on its own: it keeps one listen-only reader open
/// on the receiver's event collection, hands every frame to <see cref="Decode"/>, and reports the state it returns as the state of the
/// receiver's route. Nothing is ever written, and no request is made to find out whether a device is awake.
/// </summary>
/// <remarks>
/// The event collection usually sits beside the route's collection on the same HID interface (for example the short-report and
/// long-report collections of one receiver); they are matched by <see cref="HidCollectionPaths.InterfaceKey"/>. The state is
/// <see cref="RouteState.Unknown"/> until the first event and again after the reader failed; discovery replaces a failed reader on its
/// next scan.
/// </remarks>
public abstract class UsbEventStateMonitor : IUsbRouteStateSource, IAsyncDisposable
{
    private readonly IHidCollectionProvider _provider;
    private readonly IBatterioLog _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, EventListener> _listeners = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    protected UsbEventStateMonitor(IHidCollectionProvider provider, IBatterioLog? log)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _log = log ?? new NullBatterioLog();
    }

    public event EventHandler? StateChanged;

    /// <summary>The collection whose frames announce the state.</summary>
    protected abstract HidCollectionQuery EventQuery { get; }

    /// <summary>The collections of the device's route, whose state this monitor reports.</summary>
    protected abstract HidCollectionQuery RouteQuery { get; }

    /// <summary>What is being listened to, for the log.</summary>
    protected abstract string Description { get; }

    /// <summary>The state a frame announces, or null if it is not an announcement of this monitor.</summary>
    protected abstract RouteState? Decode(ReadOnlySpan<byte> frame);

    public IReadOnlyCollection<HidCollectionQuery> Queries => [EventQuery];

    public RouteState? GetState(HidCollectionInfo collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (!RouteQuery.Matches(collection))
        {
            return null;
        }

        lock (_gate)
        {
            return _listeners.TryGetValue(HidCollectionPaths.InterfaceKey(collection.DevicePath), out var listener) && !listener.IsClosed
                ? listener.State
                : RouteState.Unknown;
        }
    }

    public async Task SyncAsync(IReadOnlyList<HidCollectionInfo> collections, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collections);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var present = collections
            .Where(EventQuery.Matches)
            .DistinctBy(collection => collection.DevicePath, StringComparer.OrdinalIgnoreCase)
            .GroupBy(collection => HidCollectionPaths.InterfaceKey(collection.DevicePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var stale = new List<EventListener>();
        var started = new List<EventListener>();
        lock (_gate)
        {
            foreach (var pair in _listeners.ToArray())
            {
                if (!present.ContainsKey(pair.Key) || pair.Value.IsClosed)
                {
                    _listeners.Remove(pair.Key);
                    stale.Add(pair.Value);
                }
            }

            foreach (var pair in present)
            {
                if (_listeners.ContainsKey(pair.Key))
                {
                    continue;
                }

                var listener = new EventListener(_provider, pair.Value, Decode, _log);
                listener.StateChanged += OnListenerStateChanged;
                _listeners[pair.Key] = listener;
                started.Add(listener);
            }
        }

        foreach (var listener in stale)
        {
            listener.StateChanged -= OnListenerStateChanged;
            await listener.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var listener in started)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _log.Log(BatterioLogLevel.Information, $"Listening for {Description} on '{listener.Path}'.");
            listener.Start();
        }
    }

    private void OnListenerStateChanged(object? sender, EventArgs e)
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // A subscriber must not break the listener.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        EventListener[] listeners;
        lock (_gate)
        {
            listeners = _listeners.Values.ToArray();
            _listeners.Clear();
        }

        foreach (var listener in listeners)
        {
            listener.StateChanged -= OnListenerStateChanged;
            await listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    private delegate RouteState? FrameDecoder(ReadOnlySpan<byte> frame);

    /// <summary>A long-lived, listen-only reader on one event collection.</summary>
    private sealed class EventListener(IHidCollectionProvider provider, HidCollectionInfo collection, FrameDecoder decode, IBatterioLog log)
        : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        private readonly object _gate = new();
        private Task? _loop;
        private RouteState _state = RouteState.Unknown;
        private bool _closed;
        private int _disposed;

        public string Path => collection.DevicePath;

        public event EventHandler? StateChanged;

        public RouteState State
        {
            get
            {
                lock (_gate)
                {
                    return _state;
                }
            }
        }

        public bool IsClosed
        {
            get
            {
                lock (_gate)
                {
                    return _closed;
                }
            }
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_loop is null && !_closed)
                {
                    _loop = Task.Run(() => RunAsync(_lifetime.Token));
                }
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                await using var transport = await provider.OpenAsync(collection, cancellationToken).ConfigureAwait(false);
                await using var transaction = await transport.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[Math.Max(collection.InputReportLength, 64)];
                while (!cancellationToken.IsCancellationRequested)
                {
                    var count = await transaction.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (decode(buffer.AsSpan(0, count)) is RouteState state)
                    {
                        SetState(state);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                log.Log(BatterioLogLevel.Warning, $"Listening to '{Path}' stopped: {exception.Message}");
            }
            finally
            {
                Close();
            }
        }

        private void SetState(RouteState state)
        {
            lock (_gate)
            {
                if (_closed || _state == state)
                {
                    return;
                }

                _state = state;
            }

            RaiseStateChanged();
        }

        private void Close()
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                _state = RouteState.Unknown;
            }

            RaiseStateChanged();
        }

        private void RaiseStateChanged()
        {
            try
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                // A subscriber must not break the read loop.
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Task? loop;
            lock (_gate)
            {
                loop = _loop;
            }

            _lifetime.Cancel();
            if (loop is not null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch
                {
                    // The loop reports its own failures.
                }
            }

            Close();
            _lifetime.Dispose();
        }
    }
}

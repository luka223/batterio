using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Owns one <see cref="KeychronLinkListener"/> per Keychron Link dongle that is plugged in. Discovery calls <see cref="SyncAsync"/> after
/// every enumeration: a listener is opened when a dongle appears and closed when it goes away; one that failed is replaced by the next sync.
/// </summary>
public sealed class KeychronLinkMonitor : IUsbRouteStateSource, IAsyncDisposable
{
    private readonly IHidCollectionProvider _provider;
    private readonly TimeProvider _timeProvider;
    private readonly IBatterioLog _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, KeychronLinkListener> _listeners = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public KeychronLinkMonitor(IHidCollectionProvider provider, IBatterioLog? log = null, TimeProvider? timeProvider = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _log = log ?? new NullBatterioLog();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler? StateChanged;

    public RouteState? GetState(HidCollectionInfo collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (!KeychronQ1He.LinkRawHidQuery.Matches(collection))
        {
            return null;
        }

        lock (_gate)
        {
            return _listeners.TryGetValue(collection.DevicePath, out var listener) && !listener.IsClosed
                ? listener.State
                : RouteState.Unknown;
        }
    }

    /// <summary>The live listener of the dongle at <paramref name="path"/>, if there is one.</summary>
    public bool TryGetListener(string path, out KeychronLinkListener listener)
    {
        lock (_gate)
        {
            if (_listeners.TryGetValue(path, out var found) && !found.IsClosed)
            {
                listener = found;
                return true;
            }
        }

        listener = null!;
        return false;
    }

    public async Task SyncAsync(IReadOnlyList<HidCollectionInfo> collections, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collections);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var present = collections
            .Where(KeychronQ1He.LinkRawHidQuery.Matches)
            .DistinctBy(collection => collection.DevicePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var presentPaths = present.Select(collection => collection.DevicePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stale = new List<KeychronLinkListener>();
        var started = new List<KeychronLinkListener>();
        lock (_gate)
        {
            foreach (var pair in _listeners.ToArray())
            {
                if (!presentPaths.Contains(pair.Key) || pair.Value.IsClosed)
                {
                    _listeners.Remove(pair.Key);
                    stale.Add(pair.Value);
                }
            }

            foreach (var collection in present)
            {
                if (_listeners.ContainsKey(collection.DevicePath))
                {
                    continue;
                }

                var listener = new KeychronLinkListener(_provider, collection, _timeProvider, _log);
                listener.StateChanged += OnListenerStateChanged;
                _listeners[collection.DevicePath] = listener;
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
            _log.Log(BatterioLogLevel.Information, $"Listening for Keychron battery reports on '{listener.Path}'.");
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

        KeychronLinkListener[] listeners;
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
}

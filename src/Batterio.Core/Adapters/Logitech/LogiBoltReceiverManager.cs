using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>A paired device found in a receiver's pairing table.</summary>
public sealed record LogiBoltMatch(LogiBoltReceiver Receiver, LogiBoltSlot Slot);

/// <summary>
/// Owns the <see cref="LogiBoltReceiver"/> of every Logi Bolt receiver that is plugged in, keyed by HID interface, so that all devices
/// share one handle and one pairing table per physical receiver. Discovery calls <see cref="SyncAsync"/>; a receiver that was
/// unplugged or failed (resume from sleep) is replaced there, so there is no separate retry loop.
/// </summary>
public sealed class LogiBoltReceiverManager : IAsyncDisposable
{
    public const ushort LogitechVendorId = 0x046D;

    /// <summary>Logi Bolt receiver, HID++ on USB interface 2 (Solaar <c>base_usb.py</c>, <c>_bolt_receiver</c>).</summary>
    public const ushort BoltReceiverProductId = 0xC548;

    public const ushort BoltReceiverInterfaceNumber = 2;

    /// <summary>The Bolt receiver's long-report (0x11) HID++ collection.</summary>
    public static readonly HidCollectionQuery BoltReceiverQuery = new()
    {
        VendorId = LogitechVendorId,
        ProductId = BoltReceiverProductId,
        InterfaceNumber = BoltReceiverInterfaceNumber,
        UsagePage = 0xFF00,
        MinimumReportLength = 20,
        RequiredReportId = 0x11
    };

    /// <summary>The Bolt receiver's short-report (0x10) HID++ collection, where it sends HID++ 1.0 error replies.</summary>
    public static readonly HidCollectionQuery BoltReceiverShortQuery = new()
    {
        VendorId = LogitechVendorId,
        ProductId = BoltReceiverProductId,
        InterfaceNumber = BoltReceiverInterfaceNumber,
        UsagePage = 0xFF00,
        MinimumReportLength = 7,
        RequiredReportId = 0x10
    };

    private readonly IHidCollectionProvider _provider;
    private readonly IBatterioLog _log;
    private readonly LogiBoltReceiverOptions? _options;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, LogiBoltReceiver> _receivers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _openFailures = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public LogiBoltReceiverManager(IHidCollectionProvider provider, IBatterioLog? log = null, LogiBoltReceiverOptions? options = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _log = log ?? new NullBatterioLog();
        _options = options;
    }

    /// <summary>A device on some receiver connected (new link, or woke up).</summary>
    public event EventHandler<LogiBoltSlotEventArgs>? DeviceConnected;

    public event EventHandler<LogiBoltSlotEventArgs>? DeviceDisconnected;

    /// <summary>A device was paired, un-paired or replaced on some receiver.</summary>
    public event EventHandler<LogiBoltSlotEventArgs>? PairingChanged;

    /// <summary>A receiver failed and is waiting to be replaced by the next <see cref="SyncAsync"/>.</summary>
    public event EventHandler? ReceiverClosed;

    public IReadOnlyList<LogiBoltReceiver> Receivers
    {
        get
        {
            lock (_gate)
            {
                return _receivers.Values.OrderBy(receiver => receiver.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
    }

    /// <summary>
    /// Opens receivers that are new, disposes receivers that are gone or failed, and refreshes scan-based pairing tables.
    /// <paramref name="collections"/> are the HID collections found by discovery; those that are not Bolt receiver collections are ignored.
    /// A receiver that cannot be opened is logged once and tried again on the next sync.
    /// </summary>
    public async Task SyncAsync(IEnumerable<HidCollectionInfo> collections, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collections);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var found = collections.ToArray();
        var longs = found
            .Where(BoltReceiverQuery.Matches)
            .DistinctBy(collection => collection.DevicePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var shorts = found.Where(BoltReceiverShortQuery.Matches).ToArray();

        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var presentKeys = longs
                .Select(collection => HidCollectionPaths.InterfaceKey(collection.DevicePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var key in Snapshot().Where(pair => !presentKeys.Contains(pair.Key) || pair.Value.IsClosed).Select(pair => pair.Key).ToArray())
            {
                await RemoveAsync(key).ConfigureAwait(false);
            }

            _openFailures.Keys.Where(key => !presentKeys.Contains(key)).ToList().ForEach(key => _openFailures.Remove(key));

            foreach (var collection in longs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = HidCollectionPaths.InterfaceKey(collection.DevicePath);
                lock (_gate)
                {
                    if (_receivers.ContainsKey(key))
                    {
                        continue;
                    }
                }

                var sibling = shorts.FirstOrDefault(candidate =>
                    !string.Equals(candidate.DevicePath, collection.DevicePath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(HidCollectionPaths.InterfaceKey(candidate.DevicePath), key, StringComparison.OrdinalIgnoreCase));
                await OpenAsync(key, collection, sibling, cancellationToken).ConfigureAwait(false);
            }

            foreach (var receiver in Snapshot().Values)
            {
                try
                {
                    await receiver.RescanAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (BatteryAdapterException exception)
                {
                    _log.Log(BatterioLogLevel.Warning, $"Logi Bolt receiver '{receiver.Path}' could not be rescanned: {exception.Message}");
                }
            }
        }
        finally
        {
            _syncGate.Release();
        }
    }

    /// <summary>Every paired device on any receiver that matches the wireless product ID (or, for a scan, one of the names).</summary>
    public IReadOnlyList<LogiBoltMatch> FindAll(ushort wirelessProductId, IReadOnlySet<string>? names = null) =>
        Receivers
            .Where(receiver => !receiver.IsClosed)
            .SelectMany(receiver => receiver.Slots
                .Where(slot => slot.Matches(wirelessProductId, names))
                .Select(slot => new LogiBoltMatch(receiver, slot)))
            .ToArray();

    public bool TryGetReceiver(string path, out LogiBoltReceiver receiver)
    {
        lock (_gate)
        {
            var found = _receivers.Values.FirstOrDefault(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));
            receiver = found!;
            return found is not null && !found.IsClosed;
        }
    }

    private Dictionary<string, LogiBoltReceiver> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, LogiBoltReceiver>(_receivers, StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task OpenAsync(string key, HidCollectionInfo collection, HidCollectionInfo? shortCollection, CancellationToken cancellationToken)
    {
        var receiver = new LogiBoltReceiver(_provider, collection, shortCollection, _log, _options);
        try
        {
            await receiver.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is BatteryAdapterException or IOException)
        {
            // OpenAsync disposed the receiver. Log a persistent failure once, not on every sync.
            if (!_openFailures.TryGetValue(key, out var previous) || previous != exception.Message)
            {
                _openFailures[key] = exception.Message;
                _log.Log(BatterioLogLevel.Warning, $"Logi Bolt receiver '{collection.DevicePath}' could not be opened: {exception.Message}");
            }

            return;
        }

        _openFailures.Remove(key);
        receiver.DeviceConnected += OnDeviceConnected;
        receiver.DeviceDisconnected += OnDeviceDisconnected;
        receiver.PairingChanged += OnPairingChanged;
        receiver.Closed += OnReceiverClosed;
        lock (_gate)
        {
            _receivers[key] = receiver;
        }

        _log.Log(BatterioLogLevel.Information, $"Logi Bolt receiver '{collection.DevicePath}' opened.");
    }

    private async Task RemoveAsync(string key)
    {
        LogiBoltReceiver? receiver;
        lock (_gate)
        {
            if (!_receivers.Remove(key, out receiver))
            {
                return;
            }
        }

        receiver.DeviceConnected -= OnDeviceConnected;
        receiver.DeviceDisconnected -= OnDeviceDisconnected;
        receiver.PairingChanged -= OnPairingChanged;
        receiver.Closed -= OnReceiverClosed;
        await receiver.DisposeAsync().ConfigureAwait(false);
        _log.Log(BatterioLogLevel.Information, $"Logi Bolt receiver '{receiver.Path}' was closed.");
    }

    private void OnDeviceConnected(object? sender, LogiBoltSlotEventArgs e) => Forward(DeviceConnected, e);

    private void OnDeviceDisconnected(object? sender, LogiBoltSlotEventArgs e) => Forward(DeviceDisconnected, e);

    private void OnPairingChanged(object? sender, LogiBoltSlotEventArgs e) => Forward(PairingChanged, e);

    private void OnReceiverClosed(object? sender, EventArgs e)
    {
        try
        {
            ReceiverClosed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Warning, "A Logi Bolt receiver manager listener failed.", exception);
        }
    }

    private void Forward(EventHandler<LogiBoltSlotEventArgs>? handler, LogiBoltSlotEventArgs e)
    {
        try
        {
            handler?.Invoke(this, e);
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Warning, "A Logi Bolt receiver manager listener failed.", exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _syncGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var key in Snapshot().Keys.ToArray())
            {
                await RemoveAsync(key).ConfigureAwait(false);
            }
        }
        finally
        {
            _syncGate.Release();
        }
    }
}

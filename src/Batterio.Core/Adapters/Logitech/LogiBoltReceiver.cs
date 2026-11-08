using System.Threading.Channels;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>Timeouts of a <see cref="LogiBoltReceiver"/>; tests shorten them.</summary>
public sealed record LogiBoltReceiverOptions
{
    /// <summary>How long to wait for the receiver's arrival notices after asking for them, before falling back to a slot scan.</summary>
    public TimeSpan NoticeTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long to keep collecting notices after the first one (the receiver announces its devices in a burst).</summary>
    public TimeSpan NoticeSettleDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a fallback scan waits for a slot to answer its ping.</summary>
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromMilliseconds(750);

    /// <summary>How long one device request (several HID++ messages) may take.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long a HID++ 1.0 register request to the receiver itself may take.</summary>
    public TimeSpan RegisterTimeout { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>Where a receiver's pairing table came from.</summary>
public enum LogiBoltPairingSource
{
    /// <summary>Nothing is known yet.</summary>
    None,

    /// <summary>From the receiver's connection notices (0x41): presence and online state without waking any device.</summary>
    Notices,

    /// <summary>From probing every slot with a ping and an identity check, because no notices arrived.</summary>
    Scan
}

/// <summary>One paired device, as far as the receiver reported.</summary>
/// <param name="Slot">Pairing slot, 1 to 6; the HID++ device index.</param>
/// <param name="Wpid">Wireless product ID (for Logi Bolt devices the same as the BLE model ID, e.g. 0xB034 for the MX Master 3S).</param>
/// <param name="Online">Whether the link to the device is up.</param>
/// <param name="Name">Device name; known only from a scan.</param>
/// <param name="ModelIds">All model IDs the device reported; just the WPID when the slot came from notices.</param>
public sealed record LogiBoltSlot(
    byte Slot,
    ushort? Wpid,
    bool Online,
    string? Name,
    IReadOnlyList<ushort> ModelIds,
    LogiBoltPairingSource Source)
{
    /// <summary>A slot matches when its WPID or any model ID equals <paramref name="wirelessProductId"/>, or its name is one of <paramref name="names"/>.</summary>
    public bool Matches(ushort wirelessProductId, IReadOnlySet<string>? names) =>
        Wpid == wirelessProductId ||
        ModelIds.Contains(wirelessProductId) ||
        (names is { Count: > 0 } && !string.IsNullOrWhiteSpace(Name) && names.Contains(Name.Trim(), StringComparer.OrdinalIgnoreCase));
}

public sealed class LogiBoltSlotEventArgs(string receiverPath, LogiBoltSlot slot) : EventArgs
{
    public string ReceiverPath { get; } = receiverPath;

    public LogiBoltSlot Slot { get; } = slot;
}

/// <summary>
/// One physical Logi Bolt receiver: a long-lived handle on its HID++ collections, its pairing table, and its connect/disconnect notices.
/// </summary>
/// <remarks>
/// <para><b>Experimental, never run against real hardware.</b> Every byte layout below was copied from Solaar
/// (<c>pwr-Solaar/Solaar</c>, <c>lib/logitech_receiver</c>) and is pinned by scripted tests only. If the notice path does not
/// work on a real receiver, <see cref="OpenAsync"/> falls back to identifying the slots with the ping and identity check that
/// the previous implementation used, caches the result and logs that notices were unavailable.</para>
/// <para>Protocol notes (Solaar file → fact):</para>
/// <list type="bullet">
/// <item><c>base.py</c>: receiver register access uses device index 0xFF. A short HID++ 1.0 request is
/// <c>10 FF sub addr p0 p1 p2</c> with sub 0x81 = read register, 0x80 = write register, 0x83 = read long register. The reply echoes
/// sub and address (<c>10 FF sub addr data…</c>). An error reply is <c>10 FF 8F sub addr code</c> (code 0x01 invalid sub-ID, 0x02 invalid
/// address, 0x03 invalid value, 0x04 connection request failed, 0x05 too many devices, 0x06 already exists, 0x07 busy, 0x08 unknown
/// device, 0x09 resource error, 0x0A request unavailable, 0x0B unsupported parameter value, 0x0C wrong PIN code; <c>hidpp10_constants.py</c>).
/// Short reports are the right frame for register access, so requests go to the receiver's short collection when it exists.</item>
/// <item><c>hidpp10_constants.py</c> / <c>receiver.py</c>: the notification flags are register 0x00 (3 bytes, big-endian value).
/// <c>WIRELESS</c> = 0x000100, i.e. bit 0x01 of the middle byte, makes the receiver send connection notices. Solaar enables
/// <c>WIRELESS | SOFTWARE_PRESENT</c> (0x000900) in <c>enable_connection_notifications</c>; this class only sets the wireless bit, and
/// only when it is missing, so it does not claim to be controlling the device (<c>SOFTWARE_PRESENT</c> = 0x000800).</item>
/// <item><c>receiver.py</c> <c>notify_devices</c>: writing register 0x02 (<c>RECEIVER_CONNECTION</c>) with the single byte 0x02 makes the
/// receiver announce every paired device: <c>10 FF 80 02 02 00 00</c>.</item>
/// <item><c>notifications.py</c> (<c>_process_hidpp10_notification</c>) and <c>common.py</c>: sub-ID 0x41 (<c>DJ_PAIRING</c>) is a device
/// connection notice. Report byte 1 is the slot, byte 3 the protocol ("address"; 0x02 is the old 27 MHz protocol and 0x00 is unknown, both
/// ignored; Solaar comments that Bolt uses 0x10 and is always encrypted). Byte 4 holds flags in its high nibble: 0x40 set means the
/// link is <i>down</i> (<c>link_established = not (flags &amp; 0x40)</c>), 0x20 encrypted, 0x10 software present. The WPID is
/// <c>data[2] data[1]</c>, i.e. byte 5 is the low byte and byte 6 the high byte. Sub-ID 0x40 (<c>CONNECT_DISCONNECT</c>) with
/// address 0x02 means the device was un-paired.</item>
/// <item><c>receiver.py</c> <c>BoltReceiver.device_pairing_information</c> (not used here; the notices carry the WPID): long register
/// read 0x83 of register 0x2B5 with parameter 0x50 + slot; WPID is reply bytes 3 then 2 of the data, kind in byte 1 (low nibble),
/// serial bytes 4 to 7. Kept as the documented alternative should the notices prove unreliable.</item>
/// </list>
/// </remarks>
public sealed class LogiBoltReceiver : IAsyncDisposable
{
    public const int SlotCount = 6;

    private const byte ShortReportId = 0x10;
    private const byte LongReportId = 0x11;
    private const byte ReceiverIndex = 0xFF;
    private const byte WriteRegisterSubId = 0x80;
    private const byte ReadRegisterSubId = 0x81;
    private const byte ErrorSubId = 0x8F;
    private const byte NotificationFlagsRegister = 0x00;
    private const byte ReceiverConnectionRegister = 0x02;
    private const byte ConnectDisconnectNotice = 0x40;
    private const byte ConnectionNotice = 0x41;
    private const byte WirelessFlagMask = 0x01; // byte 1 of the 3-byte value: 0x000100

    private readonly IHidCollectionProvider _provider;
    private readonly HidCollectionInfo _collection;
    private readonly HidCollectionInfo? _shortCollection;
    private readonly IBatterioLog _log;
    private readonly LogiBoltReceiverOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _tableGate = new();
    private readonly Dictionary<byte, LogiBoltSlot> _slots = new();
    private readonly TaskCompletionSource _firstNotice = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IHidReportTransport? _transport;
    private IHidReportTransaction? _transaction;
    private IHidReportTransport? _shortTransport;
    private IHidReportTransaction? _shortTransaction;
    private MergedHidReportTransaction? _merged;
    private Task? _pump;
    private volatile ReplyChannel? _active;
    private volatile bool _raiseEvents;
    private int _noticeCount;
    private int _closed;
    private int _disposed;
    private LogiBoltPairingSource _source;

    public LogiBoltReceiver(
        IHidCollectionProvider provider,
        HidCollectionInfo collection,
        HidCollectionInfo? shortCollection,
        IBatterioLog? log = null,
        LogiBoltReceiverOptions? options = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        _shortCollection = shortCollection;
        _log = log ?? new NullBatterioLog();
        _options = options ?? new LogiBoltReceiverOptions();
    }

    /// <summary>HID path of the receiver's long-report collection; the key under which routes refer to this receiver.</summary>
    public string Path => _collection.DevicePath;

    /// <summary>Shared by the receiver's short and long collections.</summary>
    public string InterfaceKey => HidCollectionPaths.InterfaceKey(_collection.DevicePath);

    /// <summary>True once the receiver failed (unplug, resume) or was disposed. A closed receiver is never reopened; the manager replaces it.</summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public LogiBoltPairingSource Source => _source;

    public IReadOnlyList<LogiBoltSlot> Slots
    {
        get
        {
            lock (_tableGate)
            {
                return _slots.Values.OrderBy(slot => slot.Slot).ToArray();
            }
        }
    }

    /// <summary>A device connected (new link, or woke up). Raised on the receiver's reader thread; handlers must return quickly.</summary>
    public event EventHandler<LogiBoltSlotEventArgs>? DeviceConnected;

    public event EventHandler<LogiBoltSlotEventArgs>? DeviceDisconnected;

    /// <summary>A device was paired, un-paired or replaced in a slot.</summary>
    public event EventHandler<LogiBoltSlotEventArgs>? PairingChanged;

    /// <summary>The receiver can no longer be used.</summary>
    public event EventHandler? Closed;

    public LogiBoltSlot? GetSlot(byte slot)
    {
        lock (_tableGate)
        {
            return _slots.TryGetValue(slot, out var entry) ? entry : null;
        }
    }

    /// <summary>
    /// Opens the collections, enables connection notices, asks the receiver to announce its devices and waits for them.
    /// Falls back to a slot scan when no notices arrive. Events are not raised while opening: the caller reads <see cref="Slots"/> afterwards.
    /// </summary>
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            _transport = await _provider.OpenAsync(_collection, cancellationToken).ConfigureAwait(false);
            _transaction = await _transport.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await OpenShortCollectionAsync(cancellationToken).ConfigureAwait(false);

            var secondary = _shortTransaction is null || _shortCollection is null
                ? Array.Empty<(IHidReportTransaction, int)>()
                : new[] { (_shortTransaction, _shortCollection.InputReportLength) };
            _merged = new MergedHidReportTransaction(_transaction, _collection.InputReportLength, secondary);
            _pump = Task.Run(PumpAsync, CancellationToken.None);

            await EnableNoticesAsync(cancellationToken).ConfigureAwait(false);
            await WaitForNoticesAsync(cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref _noticeCount) > 0)
            {
                _source = LogiBoltPairingSource.Notices;
                _log.Log(BatterioLogLevel.Information,
                    $"Logi Bolt receiver '{Path}' announced {Slots.Count} paired device(s) through connection notices.");
            }
            else
            {
                _log.Log(BatterioLogLevel.Warning,
                    $"Logi Bolt receiver '{Path}' sent no connection notices within {_options.NoticeTimeout.TotalMilliseconds:F0} ms " +
                    "(experimental notice path unavailable); identifying the paired slots with a scan instead.");
                await ScanAsync(cancellationToken).ConfigureAwait(false);
                _source = LogiBoltPairingSource.Scan;
            }

            _raiseEvents = true;
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> against the device in <paramref name="slot"/>. Requests are serialised; notices that arrive
    /// meanwhile are still dispatched. A request that is not answered in time is reported as <see cref="BatteryAdapterErrorKind.DeviceOffline"/>.
    /// </summary>
    public Task<T> ExecuteAsync<T>(
        byte slot,
        Func<HidppClient, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return WithClientAsync(async (client, token) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_options.RequestTimeout);
            try
            {
                return await work(client, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline,
                    $"Logi Bolt slot 0x{slot:X2} on '{Path}' did not answer in time.");
            }
        }, cancellationToken);
    }

    /// <summary>Repeats the fallback slot scan. Does nothing unless the pairing table came from a scan, because notices keep it current.</summary>
    public async Task RescanAsync(CancellationToken cancellationToken = default)
    {
        if (IsClosed || _source != LogiBoltPairingSource.Scan)
        {
            return;
        }

        await ScanAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task OpenShortCollectionAsync(CancellationToken cancellationToken)
    {
        if (_shortCollection is null)
        {
            return;
        }

        try
        {
            _shortTransport = await _provider.OpenAsync(_shortCollection, cancellationToken).ConfigureAwait(false);
            _shortTransaction = await _shortTransport.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (BatteryAdapterException exception)
        {
            _log.Log(BatterioLogLevel.Warning,
                $"Logi Bolt short-report collection '{_shortCollection.DevicePath}' could not be opened; register requests use long reports " +
                $"and empty slots are detected by timeout: {exception.Message}");
            if (_shortTransaction is not null) await _shortTransaction.DisposeAsync().ConfigureAwait(false);
            if (_shortTransport is not null) await _shortTransport.DisposeAsync().ConfigureAwait(false);
            _shortTransaction = null;
            _shortTransport = null;
        }
    }

    private async Task EnableNoticesAsync(CancellationToken cancellationToken)
    {
        var flags = await RegisterRequestAsync(ReadRegisterSubId, NotificationFlagsRegister, ReadOnlyMemory<byte>.Empty, cancellationToken)
            .ConfigureAwait(false);
        if (flags is { Length: >= 3 })
        {
            _log.Log(BatterioLogLevel.Information,
                $"Logi Bolt receiver '{Path}' notification flags: 0x{flags[0]:X2}{flags[1]:X2}{flags[2]:X2}.");
            if ((flags[1] & WirelessFlagMask) == 0)
            {
                var enabled = new[] { flags[0], (byte)(flags[1] | WirelessFlagMask), flags[2] };
                await RegisterRequestAsync(WriteRegisterSubId, NotificationFlagsRegister, enabled, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            _log.Log(BatterioLogLevel.Warning,
                $"Logi Bolt receiver '{Path}' did not return its notification flags; asking for the device announcements anyway.");
        }

        await RegisterRequestAsync(WriteRegisterSubId, ReceiverConnectionRegister, new byte[] { 0x02, 0x00, 0x00 }, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WaitForNoticesAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _noticeCount) == 0)
        {
            await Task.WhenAny(_firstNotice.Task, Task.Delay(_options.NoticeTimeout, cancellationToken)).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _noticeCount) > 0)
        {
            // The receiver announces its devices in a burst; let the rest of it arrive.
            await Task.Delay(_options.NoticeSettleDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Sends a HID++ 1.0 register request to the receiver and returns the reply data; null if rejected or unanswered.</summary>
    private Task<byte[]?> RegisterRequestAsync(byte subId, byte address, ReadOnlyMemory<byte> parameters, CancellationToken cancellationToken) =>
        RunExclusiveAsync<byte[]?>(async (channel, token) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_options.RegisterTimeout);
            await WriteAsync(BuildRegisterFrame(subId, address, parameters.Span), timeout.Token).ConfigureAwait(false);

            var buffer = new byte[64];
            try
            {
                while (true)
                {
                    var count = await channel.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (count < 4 || buffer[1] != ReceiverIndex)
                    {
                        continue;
                    }

                    if (buffer[2] == ErrorSubId && count >= 6 && buffer[3] == subId && buffer[4] == address)
                    {
                        _log.Log(BatterioLogLevel.Warning,
                            $"Logi Bolt receiver '{Path}' rejected register 0x{address:X2} (sub-ID 0x{subId:X2}) with error 0x{buffer[5]:X2}.");
                        return null;
                    }

                    if (buffer[2] == subId && buffer[3] == address)
                    {
                        return buffer.AsSpan(4, count - 4).ToArray();
                    }
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                _log.Log(BatterioLogLevel.Warning,
                    $"Logi Bolt receiver '{Path}' did not answer register 0x{address:X2} (sub-ID 0x{subId:X2}).");
                return null;
            }
        }, cancellationToken);

    private byte[] BuildRegisterFrame(byte subId, byte address, ReadOnlySpan<byte> parameters)
    {
        // Register access is defined for short reports; the long form is only used when the short collection is unavailable.
        var useShort = _shortTransaction is not null && _shortCollection is not null;
        var frame = new byte[useShort ? Math.Max(7, _shortCollection!.OutputReportLength) : Math.Max(20, _collection.OutputReportLength)];
        frame[0] = useShort ? ShortReportId : LongReportId;
        frame[1] = ReceiverIndex;
        frame[2] = subId;
        frame[3] = address;
        parameters[..Math.Min(parameters.Length, useShort ? 3 : 16)].CopyTo(frame.AsSpan(4));
        return frame;
    }

    private ValueTask WriteAsync(byte[] frame, CancellationToken cancellationToken) =>
        frame[0] == ShortReportId && _shortTransaction is not null
            ? _shortTransaction.WriteAsync(frame, cancellationToken)
            : _merged!.WriteAsync(frame, cancellationToken);

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        var found = new Dictionary<byte, LogiBoltSlot>();
        for (var number = 1; number <= SlotCount; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slot = (byte)number;
            try
            {
                var identity = await WithClientAsync(async (client, token) =>
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(_options.RequestTimeout);
                    try
                    {
                        return await HidppDeviceIdentity.ReadAsync(
                            client, slot, _options.PingTimeout, $"Logi Bolt receiver '{Path}' slot 0x{slot:X2}", timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        throw new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline, "The slot did not answer in time.");
                    }
                }, cancellationToken).ConfigureAwait(false);

                _log.Log(BatterioLogLevel.Information,
                    $"Logi Bolt receiver '{Path}' slot 0x{slot:X2}: name '{identity.Name ?? "unavailable"}', {identity.ModelText}.");
                found[slot] = new LogiBoltSlot(slot, identity.ModelIds.Count > 0 ? identity.ModelIds[0] : null, true,
                    identity.Name, identity.ModelIds, LogiBoltPairingSource.Scan);
            }
            catch (BatteryAdapterException exception) when (exception.Kind is
                BatteryAdapterErrorKind.DeviceOffline or BatteryAdapterErrorKind.UnsupportedCapability or
                BatteryAdapterErrorKind.ProtocolError or BatteryAdapterErrorKind.MalformedResponse or BatteryAdapterErrorKind.DeviceBusy)
            {
                _log.Log(BatterioLogLevel.Trace, $"Logi Bolt receiver '{Path}' slot 0x{slot:X2} was unavailable: {exception.Message}");
            }
        }

        ApplyScan(found);
    }

    private void ApplyScan(Dictionary<byte, LogiBoltSlot> found)
    {
        var events = new List<(EventHandler<LogiBoltSlotEventArgs>? Handler, LogiBoltSlot Slot)>();
        lock (_tableGate)
        {
            foreach (var stale in _slots.Values
                         .Where(slot => slot.Source == LogiBoltPairingSource.Scan && !found.ContainsKey(slot.Slot)).ToArray())
            {
                _slots.Remove(stale.Slot);
                events.Add((PairingChanged, stale));
            }

            foreach (var slot in found.Values)
            {
                if (_slots.TryGetValue(slot.Slot, out var existing) && existing.Source == LogiBoltPairingSource.Notices)
                {
                    continue; // notices are more current than a scan
                }

                _slots.TryGetValue(slot.Slot, out var previous);
                _slots[slot.Slot] = slot;
                if (previous is null || previous.Wpid != slot.Wpid) events.Add((PairingChanged, slot));
                if (previous is null || !previous.Online || previous.Wpid != slot.Wpid) events.Add((DeviceConnected, slot));
            }
        }

        foreach (var (handler, slot) in events) Raise(handler, slot);
    }

    private Task<T> WithClientAsync<T>(Func<HidppClient, CancellationToken, Task<T>> work, CancellationToken cancellationToken) =>
        RunExclusiveAsync(async (channel, token) =>
        {
            var client = new HidppClient(
                new TracingHidTransaction(new RequestTransaction(this, channel), _log, Path), _collection, _log);
            return await work(client, token).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<T> RunExclusiveAsync<T>(Func<ReplyChannel, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        await using var protocolGate = await LogitechProtocolGate.EnterAsync(Path, cancellationToken).ConfigureAwait(false);
        ThrowIfClosed();
        var channel = new ReplyChannel();
        _active = channel;
        try
        {
            return await work(channel, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            Close($"Writing to or reading from Logi Bolt receiver '{Path}' failed: {exception.Message}", exception);
            throw new BatteryAdapterException(BatteryAdapterErrorKind.AccessFailure,
                $"Could not communicate with Logi Bolt receiver '{Path}'.", exception);
        }
        finally
        {
            _active = null;
            channel.Complete();
        }
    }

    private void ThrowIfClosed()
    {
        if (IsClosed)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.AccessFailure, $"Logi Bolt receiver '{Path}' is closed.");
        }
    }

    private async Task PumpAsync()
    {
        var merged = _merged!;
        var buffer = new byte[Math.Max(64, _collection.InputReportLength)];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var count = await merged.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (count <= 0)
                {
                    Close($"Logi Bolt receiver '{Path}' stopped delivering reports.", null);
                    return;
                }

                Dispatch(buffer.AsSpan(0, count));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Close($"Reading from Logi Bolt receiver '{Path}' failed: {exception.Message}", exception);
        }
    }

    private void Dispatch(ReadOnlySpan<byte> report)
    {
        if (report.Length >= 4 && report[0] is ShortReportId or LongReportId && report[2] is ConnectDisconnectNotice or ConnectionNotice)
        {
            try
            {
                HandleNotice(report);
            }
            catch (Exception exception)
            {
                _log.Log(BatterioLogLevel.Warning, $"Logi Bolt receiver '{Path}' could not process a notice.", exception);
            }

            return;
        }

        _active?.Post(report.ToArray());
    }

    private void HandleNotice(ReadOnlySpan<byte> report)
    {
        var slot = report[1];
        var address = report[3];
        if (slot is < 1 or > SlotCount)
        {
            _log.Log(BatterioLogLevel.Trace, $"Ignored Logi Bolt notice 0x{report[2]:X2} for device index 0x{slot:X2}.");
            return;
        }

        if (report[2] == ConnectDisconnectNotice)
        {
            if (address == 0x02)
            {
                _log.Log(BatterioLogLevel.Information, $"Logi Bolt receiver '{Path}': device in slot 0x{slot:X2} was un-paired.");
                RemoveSlot(slot);
            }

            return;
        }

        // Sub-ID 0x41 needs the flags and the two WPID bytes: 10 slot 41 addr flags wpidLow wpidHigh.
        if (report.Length < 7 || address is 0x00 or 0x02)
        {
            _log.Log(BatterioLogLevel.Trace,
                $"Ignored Logi Bolt connection notice for slot 0x{slot:X2} with protocol 0x{address:X2} ({report.Length} bytes).");
            return;
        }

        var flags = report[4] & 0xF0;
        var linkUp = (flags & 0x40) == 0;
        var wpid = (ushort)((report[6] << 8) | report[5]);
        Interlocked.Increment(ref _noticeCount);
        _log.Log(BatterioLogLevel.Information,
            $"Logi Bolt receiver '{Path}' connection notice: slot 0x{slot:X2}, WPID 0x{wpid:X4}, protocol 0x{address:X2}, link {(linkUp ? "up" : "down")}, flags 0x{flags:X2}.");
        SetSlotFromNotice(slot, wpid, linkUp);
        _firstNotice.TrySetResult();
    }

    private void SetSlotFromNotice(byte slot, ushort wpid, bool online)
    {
        LogiBoltSlot? previous;
        LogiBoltSlot next;
        lock (_tableGate)
        {
            _slots.TryGetValue(slot, out previous);
            if (previous is { Source: LogiBoltPairingSource.Notices } && previous.Wpid == wpid && previous.Online == online)
            {
                return;
            }

            var sameDevice = previous?.Wpid == wpid;
            next = new LogiBoltSlot(slot, wpid, online, sameDevice ? previous!.Name : null, new[] { wpid }, LogiBoltPairingSource.Notices);
            _slots[slot] = next;
        }

        var changedPairing = previous is null || previous.Wpid != wpid;
        if (changedPairing) Raise(PairingChanged, next);
        if (online && (changedPairing || previous is { Online: false } || previous is { Source: LogiBoltPairingSource.Scan })) Raise(DeviceConnected, next);
        else if (!online && !changedPairing && previous is { Online: true }) Raise(DeviceDisconnected, next);
    }

    private void RemoveSlot(byte slot)
    {
        LogiBoltSlot? removed;
        lock (_tableGate)
        {
            if (!_slots.Remove(slot, out removed))
            {
                return;
            }
        }

        Raise(PairingChanged, removed);
    }

    private void Raise(EventHandler<LogiBoltSlotEventArgs>? handler, LogiBoltSlot slot)
    {
        if (!_raiseEvents || handler is null)
        {
            return;
        }

        try
        {
            handler(this, new LogiBoltSlotEventArgs(Path, slot));
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Warning, "A Logi Bolt receiver listener failed.", exception);
        }
    }

    private void Close(string reason, Exception? exception)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        var disposing = Volatile.Read(ref _disposed) != 0;
        if (!disposing)
        {
            _log.Log(BatterioLogLevel.Warning, reason, exception);
        }

        _active?.Fail(new IOException(reason, exception));
        _firstNotice.TrySetCanceled();
        if (!disposing && _raiseEvents)
        {
            try
            {
                Closed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception listenerFailure)
            {
                _log.Log(BatterioLogLevel.Warning, "A Logi Bolt receiver listener failed.", listenerFailure);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Close("Logi Bolt receiver disposed.", null);
        _stop.Cancel();
        if (_pump is not null)
        {
            try { await _pump.ConfigureAwait(false); }
            catch (Exception) { }
        }

        if (_merged is not null) await _merged.DisposeAsync().ConfigureAwait(false);
        if (_shortTransaction is not null) await _shortTransaction.DisposeAsync().ConfigureAwait(false);
        if (_shortTransport is not null) await _shortTransport.DisposeAsync().ConfigureAwait(false);
        if (_transaction is not null) await _transaction.DisposeAsync().ConfigureAwait(false);
        if (_transport is not null) await _transport.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    /// <summary>The replies the pump routes to the request that is currently running.</summary>
    private sealed class ReplyChannel
    {
        private readonly Channel<byte[]> _reports = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

        public void Post(byte[] report) => _reports.Writer.TryWrite(report);

        public void Fail(Exception exception) => _reports.Writer.TryComplete(exception);

        public void Complete() => _reports.Writer.TryComplete();

        public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            try
            {
                var report = await _reports.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var count = Math.Min(report.Length, destination.Length);
                report.AsSpan(0, count).CopyTo(destination.Span);
                return count;
            }
            catch (ChannelClosedException exception)
            {
                throw exception.InnerException as IOException ?? new IOException("The Logi Bolt receiver was closed.", exception);
            }
        }
    }

    /// <summary>The view of the receiver one request gets: writes go to the receiver, reads come from the pump's reply queue.</summary>
    private sealed class RequestTransaction(LogiBoltReceiver owner, ReplyChannel channel) : IHidReportTransaction
    {
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default) =>
            owner._merged!.WriteAsync(report, cancellationToken);

        public ValueTask<int> ReadAsync(Memory<byte> report, CancellationToken cancellationToken = default) =>
            channel.ReadAsync(report, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

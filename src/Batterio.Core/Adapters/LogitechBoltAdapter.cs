using System.Diagnostics;
using System.Text.Json;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Identifies a Logitech device that is paired to a Logi Bolt receiver or connected directly over Bluetooth LE.
/// </summary>
/// <remarks>
/// Sources and hardware confirmation:
/// <list type="bullet">
/// <item>Bolt receiver 0xC548, HID++ on USB interface 2, up to 6 paired slots: Solaar <c>base_usb.py</c> (<c>_bolt_receiver</c>).</item>
/// <item>MX Master 3S reports product ID 0xB034 and MX Mechanical 0xB366, confirmed on hardware over Bluetooth LE: Windows lists
/// them as <c>VID&amp;02046D_PID&amp;B034</c> / <c>PID&amp;B366</c>, and their Device Name (0x0005) is "MX Master 3S" / "MX Mechanical".</item>
/// <item>Over Bluetooth the HID++ channel is the vendor collection 0xFF43/0x0202 (long reports 0x11) and the device answers on
/// device index 0xFF, as in Solaar's direct Bluetooth support.</item>
/// <item>Device Information (0x0003) layout (transport bitmap at byte 6, model IDs from byte 7 in the order BT, BLE, eQuad, USB),
/// Device Name (0x0005), Unified Battery (0x1004) and Battery Status (0x1000): Solaar <c>hidpp20.py</c>.</item>
/// </list>
/// A device matches when any of its model IDs is expected, or its reported name is expected.
/// </remarks>
public sealed record LogitechBoltDeviceProfile(
    string AdapterId,
    string DisplayName,
    IReadOnlySet<ushort> ExpectedModelIds,
    IReadOnlySet<string> ExpectedNames,
    IReadOnlySet<ushort> BluetoothProductIds)
{
    public static LogitechBoltDeviceProfile MxMaster3s { get; } = new(
        "logitech-mx-master-3s",
        "Logitech MX Master 3S",
        new HashSet<ushort> { 0xB034 },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MX Master 3S" },
        new HashSet<ushort> { 0xB034 });

    public static LogitechBoltDeviceProfile MxKeysMechanical { get; } = new(
        "logitech-mx-keys-mechanical",
        "Logitech MX Keys Mechanical",
        new HashSet<ushort> { 0xB366 },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MX Mechanical", "MX Keys Mechanical" },
        new HashSet<ushort> { 0xB366 });

    /// <summary>HID collections through which the device is reachable when it is connected over Bluetooth LE.</summary>
    public IReadOnlyList<HidCollectionQuery> BluetoothQueries =>
        BluetoothProductIds.Order().Select(LogitechReceiverDefinitions.BluetoothHidppQuery).ToArray();

    public bool Matches(string? name, IEnumerable<ushort> modelIds) =>
        modelIds.Any(ExpectedModelIds.Contains) ||
        (!string.IsNullOrWhiteSpace(name) && ExpectedNames.Contains(name.Trim()));
}

/// <summary>
/// Reads the battery of a Logitech device connected over Bluetooth LE or paired to a Logi Bolt receiver.
/// Bluetooth endpoints are tried first; then the paired slots of every Bolt receiver are scanned.
/// </summary>
public sealed class LogitechBoltAdapter : IBatteryAdapter
{
    public const ushort VendorId = 0x046D;
    public const ushort ReceiverProductId = 0xC548;
    public const ushort InterfaceNumber = 2;

    /// <summary>HID++ device index of a device that is addressed directly rather than through a receiver.</summary>
    public const byte DirectDeviceIndex = 0xFF;

    private const ushort UnifiedBatteryFeature = 0x1004;
    private const ushort BatteryStatusFeature = 0x1000;
    private const ushort DeviceInformationFeature = 0x0003;
    private const int SlotCount = 6;
    private static readonly TimeSpan PingTimeout = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan BluetoothPingTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SlotTimeout = TimeSpan.FromSeconds(3);

    private readonly LogitechBoltDeviceProfile _profile;
    private readonly IHidCollectionProvider _provider;
    private readonly IBatterioLog _log;
    private readonly string? _devicePath;
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;
    private PreferredSlot? _preferred;

    public LogitechBoltAdapter(LogitechBoltDeviceProfile profile)
        : this(profile, new EmptyHidCollectionProvider(), null, null)
    {
    }

    public LogitechBoltAdapter(
        LogitechBoltDeviceProfile profile,
        IHidCollectionProvider provider,
        IBatterioLog? log = null,
        string? devicePath = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _log = log ?? new NullBatterioLog();
        _devicePath = devicePath;
    }

    public async Task<BatteryReading> GetBatteryStatus(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var started = Stopwatch.GetTimestamp();
        try { return await QueryAsync(attempt.Token).ConfigureAwait(false); }
        catch (BatteryAdapterException error)
        {
            _log.Log(BatterioLogLevel.Warning, $"{_profile.DisplayName} query failed ({error.Kind}): {error.Message}");
            throw;
        }
        finally
        {
            _log.Log(BatterioLogLevel.Information,
                $"{_profile.DisplayName} query finished in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
        }
    }

    private async Task<BatteryReading> QueryAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        var bluetooth = FindCollections(_profile.BluetoothQueries);
        var receivers = FindCollections(new[] { LogitechReceiverDefinitions.BoltReceiverQuery });
        if (bluetooth.Length == 0 && receivers.Length == 0)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.ReceiverMissing, _devicePath is null
                ? $"No Bluetooth connection to {_profile.DisplayName} and no Logi Bolt receiver HID++ endpoint was found."
                : $"The configured HID++ endpoint path '{_devicePath}' was not found.");
        }

        BatteryAdapterException? endpointFailure = null;
        foreach (var endpoint in bluetooth)
        {
            try
            {
                var reading = await ReadBluetoothAsync(endpoint, cancellationToken).ConfigureAwait(false);
                if (reading is not null)
                {
                    return reading;
                }
            }
            catch (BatteryAdapterException exception) when (exception.Kind is BatteryAdapterErrorKind.AccessFailure or BatteryAdapterErrorKind.DeviceBusy)
            {
                endpointFailure ??= exception;
                _log.Log(BatterioLogLevel.Warning, $"Bluetooth endpoint '{endpoint.DevicePath}' could not be used: {exception.Message}");
            }
            catch (BatteryAdapterException exception) when (exception.Kind is BatteryAdapterErrorKind.DeviceOffline)
            {
                // Windows keeps the collection of a paired Bluetooth device that is asleep or switched to another host.
                _log.Log(BatterioLogLevel.Trace, $"Bluetooth endpoint '{endpoint.DevicePath}' was unavailable: {exception.Message}");
            }
        }

        // Any outcome other than a successful read invalidates the cached slot; it is restored on success.
        var preferred = Interlocked.Exchange(ref _preferred, null);
        var ordered = receivers
            .OrderBy(receiver => preferred is not null &&
                                 string.Equals(receiver.DevicePath, preferred.Path, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToArray();

        foreach (var receiver in ordered)
        {
            try
            {
                var reading = await ScanReceiverAsync(receiver, preferred, cancellationToken).ConfigureAwait(false);
                if (reading is not null)
                {
                    return reading;
                }
            }
            catch (BatteryAdapterException exception) when (exception.Kind is BatteryAdapterErrorKind.AccessFailure or BatteryAdapterErrorKind.DeviceBusy)
            {
                // Another receiver may still hold the device.
                endpointFailure ??= exception;
                _log.Log(BatterioLogLevel.Warning, $"Bolt receiver '{receiver.DevicePath}' could not be used: {exception.Message}");
            }
        }

        if (endpointFailure is not null)
        {
            throw endpointFailure;
        }

        throw new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline,
            $"{_profile.DisplayName} did not answer over {bluetooth.Length} Bluetooth endpoint(s) and was not found in any paired slot of " +
            $"{receivers.Length} Logi Bolt receiver(s); it may be asleep, switched to another host, or not paired.");
    }

    private HidCollectionInfo[] FindCollections(IEnumerable<HidCollectionQuery> queries)
    {
        var found = new List<HidCollectionInfo>();
        foreach (var query in queries)
        {
            found.AddRange((_provider.Enumerate(query) ?? Array.Empty<HidCollectionInfo>())
                .Where(query.Matches)
                .Where(collection => _devicePath is null ||
                                     string.Equals(collection.DevicePath, _devicePath, StringComparison.OrdinalIgnoreCase)));
        }

        return found
            .DistinctBy(collection => collection.DevicePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<BatteryReading?> ReadBluetoothAsync(HidCollectionInfo endpoint, CancellationToken cancellationToken)
    {
        await using var protocolGate = await LogitechProtocolGate.EnterAsync(endpoint.DevicePath, cancellationToken).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await using var transport = await _provider.OpenAsync(endpoint, cancellationToken).ConfigureAwait(false);
        await using var transaction = await transport.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var client = new HidppClient(new TracingHidTransaction(transaction, _log, endpoint.DevicePath), endpoint, _log);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SlotTimeout);
        var matched = false;
        try
        {
            if (!await IdentifyAsync(client, endpoint, DirectDeviceIndex, BluetoothPingTimeout, timeout.Token).ConfigureAwait(false))
            {
                return null;
            }

            matched = true;
            return await ReadBatteryAsync(client, endpoint, DirectDeviceIndex, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline,
                $"{_profile.DisplayName} did not answer in time over Bluetooth endpoint '{endpoint.DevicePath}'.");
        }
        catch (BatteryAdapterException exception) when (!matched && exception.Kind is
            BatteryAdapterErrorKind.UnsupportedCapability or BatteryAdapterErrorKind.ProtocolError or BatteryAdapterErrorKind.MalformedResponse)
        {
            _log.Log(BatterioLogLevel.Trace, $"Bluetooth endpoint '{endpoint.DevicePath}' was not usable: {exception.Message}");
            return null;
        }
    }

    private async Task<BatteryReading?> ScanReceiverAsync(
        HidCollectionInfo receiver,
        PreferredSlot? preferred,
        CancellationToken cancellationToken)
    {
        await using var protocolGate = await LogitechProtocolGate.EnterAsync(receiver.DevicePath, cancellationToken).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await using var transport = await _provider.OpenAsync(receiver, cancellationToken).ConfigureAwait(false);
        await using var transaction = await transport.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var errorChannel = await OpenErrorChannelAsync(receiver, cancellationToken).ConfigureAwait(false);
        await using var merged = new MergedHidReportTransaction(transaction, receiver.InputReportLength, errorChannel.Readers);
        var client = new HidppClient(new TracingHidTransaction(merged, _log, receiver.DevicePath), receiver, _log);

        var preferredSlot = preferred is not null &&
                            string.Equals(preferred.Path, receiver.DevicePath, StringComparison.OrdinalIgnoreCase)
            ? preferred.Slot
            : (byte?)null;
        var slots = Enumerable.Range(1, SlotCount).Select(value => (byte)value)
            .OrderBy(slot => slot == preferredSlot ? 0 : 1);

        foreach (var slot in slots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var slotToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            slotToken.CancelAfter(SlotTimeout);
            var matched = false;
            try
            {
                if (!await IdentifyAsync(client, receiver, slot, PingTimeout, slotToken.Token).ConfigureAwait(false))
                {
                    continue;
                }

                matched = true;
                var reading = await ReadBatteryAsync(client, receiver, slot, slotToken.Token).ConfigureAwait(false);
                Volatile.Write(ref _preferred, new PreferredSlot(receiver.DevicePath, slot));
                return reading;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var message = $"Logi Bolt slot 0x{slot:X2} on '{receiver.DevicePath}' did not answer in time.";
                if (matched)
                {
                    throw new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline, message);
                }

                _log.Log(BatterioLogLevel.Trace, message);
            }
            catch (BatteryAdapterException exception) when (!matched && exception.Kind is
                BatteryAdapterErrorKind.DeviceOffline or BatteryAdapterErrorKind.UnsupportedCapability or
                BatteryAdapterErrorKind.ProtocolError or BatteryAdapterErrorKind.MalformedResponse)
            {
                _log.Log(BatterioLogLevel.Trace,
                    $"Logi Bolt slot 0x{slot:X2} on '{receiver.DevicePath}' was unavailable: {exception.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// Opens the receiver's short-report collection, where Windows delivers the receiver's HID++ 1.0 error replies.
    /// Without it an empty or disconnected slot is only detected by a timeout.
    /// </summary>
    private async Task<ErrorChannel> OpenErrorChannelAsync(HidCollectionInfo receiver, CancellationToken cancellationToken)
    {
        var siblingKey = HidCollectionPaths.InterfaceKey(receiver.DevicePath);
        HidCollectionInfo? sibling;
        try
        {
            var query = LogitechReceiverDefinitions.BoltReceiverShortQuery;
            sibling = (_provider.Enumerate(query) ?? Array.Empty<HidCollectionInfo>())
                .Where(query.Matches)
                .FirstOrDefault(collection =>
                    !string.Equals(collection.DevicePath, receiver.DevicePath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(HidCollectionPaths.InterfaceKey(collection.DevicePath), siblingKey, StringComparison.OrdinalIgnoreCase));
        }
        catch (BatteryAdapterException exception)
        {
            _log.Log(BatterioLogLevel.Trace, $"Bolt short-report collection lookup failed: {exception.Message}");
            return ErrorChannel.None;
        }

        if (sibling is null)
        {
            return ErrorChannel.None;
        }

        IHidReportTransport? transport = null;
        try
        {
            transport = await _provider.OpenAsync(sibling, cancellationToken).ConfigureAwait(false);
            var transaction = await transport.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            return new ErrorChannel(sibling, transport, transaction);
        }
        catch (BatteryAdapterException exception)
        {
            if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
            _log.Log(BatterioLogLevel.Warning,
                $"Bolt short-report collection '{sibling.DevicePath}' could not be opened; empty slots will be detected by timeout: {exception.Message}");
            return ErrorChannel.None;
        }
    }

    private sealed class ErrorChannel(HidCollectionInfo? collection, IHidReportTransport? transport, IHidReportTransaction? transaction)
        : IAsyncDisposable
    {
        public static ErrorChannel None => new(null, null, null);

        public IReadOnlyList<(IHidReportTransaction Transaction, int InputLength)> Readers =>
            transaction is null || collection is null
                ? Array.Empty<(IHidReportTransaction, int)>()
                : new[] { (transaction, collection.InputReportLength) };

        public async ValueTask DisposeAsync()
        {
            if (transaction is not null) await transaction.DisposeAsync().ConfigureAwait(false);
            if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Pings the device and compares its identity with the profile. Returns false when another device answers.</summary>
    private async Task<bool> IdentifyAsync(
        HidppClient client,
        HidCollectionInfo endpoint,
        byte deviceIndex,
        TimeSpan pingTimeout,
        CancellationToken cancellationToken)
    {
        var source = deviceIndex == DirectDeviceIndex
            ? $"Bluetooth endpoint '{endpoint.DevicePath}'"
            : $"Logi Bolt receiver '{endpoint.DevicePath}' slot 0x{deviceIndex:X2}";
        using (var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            probe.CancelAfter(pingTimeout);
            try
            {
                await client.PingAsync(deviceIndex, probe.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline, $"{source} did not answer its probe.");
            }
        }

        IReadOnlyList<ushort> modelIds = Array.Empty<ushort>();
        string modelText = "unavailable";
        try
        {
            var infoIndex = await client.ResolveFeatureAsync(deviceIndex, DeviceInformationFeature, cancellationToken).ConfigureAwait(false);
            var info = await client.SendAsync(deviceIndex, infoIndex, 0, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            modelIds = LogitechDeviceInformation.GetModelIds(info.Parameters).Select(entry => entry.ModelId).ToArray();
            modelText = LogitechDeviceInformation.FormatModelIds(info.Parameters);
        }
        catch (BatteryAdapterException exception) when (exception.Kind == BatteryAdapterErrorKind.UnsupportedCapability)
        {
        }

        var name = await LogitechDeviceName.ReadAsync(client, deviceIndex, cancellationToken).ConfigureAwait(false);
        var matches = _profile.Matches(name, modelIds);
        _log.Log(BatterioLogLevel.Information,
            $"{source}: name '{name ?? "unavailable"}', {modelText}; " +
            (matches ? $"matches {_profile.DisplayName}." : $"does not match {_profile.DisplayName}."));
        return matches;
    }

    private async Task<BatteryReading> ReadBatteryAsync(
        HidppClient client,
        HidCollectionInfo endpoint,
        byte deviceIndex,
        CancellationToken cancellationToken)
    {
        BatteryReading reading;
        string featureName;
        string raw;
        byte? unifiedIndex = null;
        try
        {
            unifiedIndex = await client.ResolveFeatureAsync(deviceIndex, UnifiedBatteryFeature, cancellationToken).ConfigureAwait(false);
        }
        catch (BatteryAdapterException exception) when (exception.Kind == BatteryAdapterErrorKind.UnsupportedCapability)
        {
        }

        if (unifiedIndex is byte batteryIndex)
        {
            var capabilities = await client.SendAsync(deviceIndex, batteryIndex, 0, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            var status = await client.SendAsync(deviceIndex, batteryIndex, 0x10, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            reading = LogitechBatteryDecoding.DecodeUnifiedBattery(status.Parameters,
                LogitechBatteryDecoding.SupportsPercentage(capabilities.Parameters));
            featureName = "Unified Battery 0x1004";
            raw = Convert.ToHexString(status.Parameters.AsSpan(0, Math.Min(4, status.Parameters.Length)));
        }
        else
        {
            var legacyIndex = await client.ResolveFeatureAsync(deviceIndex, BatteryStatusFeature, cancellationToken).ConfigureAwait(false);
            var status = await client.SendAsync(deviceIndex, legacyIndex, 0, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            reading = LogitechBatteryDecoding.DecodeBatteryStatus(status.Parameters);
            featureName = "Battery Status 0x1000";
            raw = Convert.ToHexString(status.Parameters.AsSpan(0, Math.Min(3, status.Parameters.Length)));
        }

        var percentageText = reading.Percentage is int percentage ? $"{percentage}%" : "unknown";
        var where = deviceIndex == DirectDeviceIndex ? "over Bluetooth" : $"slot 0x{deviceIndex:X2}";
        _log.Log(BatterioLogLevel.Information,
            $"{_profile.DisplayName} on '{endpoint.DevicePath}' {where} returned {percentageText} ({reading.ChargeState}) via {featureName}; raw status bytes {raw}.");
        return reading;
    }

    private sealed record PreferredSlot(string Path, byte Slot);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _lifetime.Cancel();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Helpers for Windows HID device interface paths.</summary>
public static class HidCollectionPaths
{
    private static readonly System.Text.RegularExpressions.Regex CollectionPart = new(
        @"&col[0-9a-f]{2}#(?<instance>[^#]*)&[0-9a-f]{4}#",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns a key shared by all top-level collections of one HID interface, so that sibling collections
    /// (for example <c>mi_02&amp;col01#7&amp;1edd123a&amp;0&amp;0000</c> and <c>mi_02&amp;col02#7&amp;1edd123a&amp;0&amp;0001</c>) can be paired.
    /// </summary>
    public static string InterfaceKey(string devicePath) =>
        CollectionPart.Replace(devicePath, match => $"#{match.Groups["instance"].Value}#", 1);
}

public sealed class LogitechMxMaster3sAdapterFactory : IBatteryAdapterFactory
{
    private readonly IHidCollectionProvider _provider;
    private readonly IBatterioLog _log;

    public LogitechMxMaster3sAdapterFactory()
        : this(new EmptyHidCollectionProvider(), null)
    {
    }

    public LogitechMxMaster3sAdapterFactory(IHidCollectionProvider provider, IBatterioLog? log = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _log = log ?? new NullBatterioLog();
    }

    public string AdapterId => LogitechBoltDeviceProfile.MxMaster3s.AdapterId;

    public void ValidateSettings(JsonElement settings) => HidAdapterSettings.ReadDevicePath(settings, AdapterId);

    public IBatteryAdapter CreateAdapter(DeviceConfiguration device) =>
        new LogitechBoltAdapter(
            LogitechBoltDeviceProfile.MxMaster3s,
            _provider,
            _log,
            HidAdapterSettings.ReadDevicePath(device.AdapterSettings, AdapterId));
}

public sealed class LogitechMxKeysMechanicalAdapterFactory : IBatteryAdapterFactory
{
    private readonly IHidCollectionProvider _provider;
    private readonly IBatterioLog _log;

    public LogitechMxKeysMechanicalAdapterFactory()
        : this(new EmptyHidCollectionProvider(), null)
    {
    }

    public LogitechMxKeysMechanicalAdapterFactory(IHidCollectionProvider provider, IBatterioLog? log = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _log = log ?? new NullBatterioLog();
    }

    public string AdapterId => LogitechBoltDeviceProfile.MxKeysMechanical.AdapterId;

    public void ValidateSettings(JsonElement settings) => HidAdapterSettings.ReadDevicePath(settings, AdapterId);

    public IBatteryAdapter CreateAdapter(DeviceConfiguration device) =>
        new LogitechBoltAdapter(
            LogitechBoltDeviceProfile.MxKeysMechanical,
            _provider,
            _log,
            HidAdapterSettings.ReadDevicePath(device.AdapterSettings, AdapterId));
}

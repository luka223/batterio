using System.Diagnostics;
using System.Text.Json;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Identifies a Logitech device that is paired to a Logi Bolt receiver.
/// </summary>
/// <remarks>
/// The values below are reverse-engineered from public sources and have NOT been verified on hardware
/// for the MX Mechanical:
/// <list type="bullet">
/// <item>Bolt receiver 0xC548, HID++ on USB interface 2, up to 6 paired slots: Solaar <c>base_usb.py</c> (<c>_bolt_receiver</c>).</item>
/// <item>MX Master 3S reports Bluetooth product ID 0xB034 (libinput merge request 847, which lists vendor 0x046D product 0xB034).</item>
/// <item>MX Mechanical model ID 0xB366 is a candidate only; the name from Device Name (0x0005) is the fallback signal.</item>
/// <item>Device Information (0x0003) layout (transport bitmap at byte 6, model IDs from byte 7 in the order BT, BLE, eQuad, USB),
/// Device Name (0x0005), Unified Battery (0x1004) and Battery Status (0x1000): Solaar <c>hidpp20.py</c>.</item>
/// </list>
/// A device matches when any of its model IDs is expected, or its reported name is expected. The adapter logs everything it
/// observes so that a tester with the real hardware can confirm or correct these values.
/// </remarks>
public sealed record LogitechBoltDeviceProfile(
    string AdapterId,
    string DisplayName,
    IReadOnlySet<ushort> ExpectedModelIds,
    IReadOnlySet<string> ExpectedNames)
{
    public static LogitechBoltDeviceProfile MxMaster3s { get; } = new(
        "logitech-mx-master-3s",
        "Logitech MX Master 3S",
        new HashSet<ushort> { 0xB034 },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MX Master 3S" });

    public static LogitechBoltDeviceProfile MxKeysMechanical { get; } = new(
        "logitech-mx-keys-mechanical",
        "Logitech MX Keys Mechanical",
        new HashSet<ushort> { 0xB366 },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MX Mechanical", "MX Keys Mechanical" });

    public bool Matches(string? name, IEnumerable<ushort> modelIds) =>
        modelIds.Any(ExpectedModelIds.Contains) ||
        (!string.IsNullOrWhiteSpace(name) && ExpectedNames.Contains(name.Trim()));
}

/// <summary>Reads the battery of a device paired to a Logi Bolt receiver, scanning the receiver's paired slots.</summary>
public sealed class LogitechBoltAdapter : IBatteryAdapter
{
    public const ushort VendorId = 0x046D;
    public const ushort ReceiverProductId = 0xC548;
    public const ushort InterfaceNumber = 2;

    private const ushort UnifiedBatteryFeature = 0x1004;
    private const ushort BatteryStatusFeature = 0x1000;
    private const ushort DeviceInformationFeature = 0x0003;
    private const int SlotCount = 6;
    private static readonly TimeSpan PingTimeout = TimeSpan.FromMilliseconds(750);
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

        var receivers = FindReceivers();
        // Any outcome other than a successful read invalidates the cached slot; it is restored on success.
        var preferred = Interlocked.Exchange(ref _preferred, null);
        var ordered = receivers
            .OrderBy(receiver => preferred is not null &&
                                 string.Equals(receiver.DevicePath, preferred.Path, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToArray();

        BatteryAdapterException? receiverFailure = null;
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
                receiverFailure ??= exception;
                _log.Log(BatterioLogLevel.Warning, $"Bolt receiver '{receiver.DevicePath}' could not be used: {exception.Message}");
            }
        }

        if (receiverFailure is not null)
        {
            throw receiverFailure;
        }

        throw new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline,
            $"{_profile.DisplayName} was not found in any paired slot of {receivers.Length} Logi Bolt receiver(s); it may be asleep or not paired.");
    }

    private HidCollectionInfo[] FindReceivers()
    {
        var query = LogitechReceiverDefinitions.BoltReceiverQuery;
        var receivers = (_provider.Enumerate(query) ?? Array.Empty<HidCollectionInfo>())
            .Where(query.Matches)
            .Where(collection => _devicePath is null ||
                                 string.Equals(collection.DevicePath, _devicePath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (receivers.Length == 0)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.ReceiverMissing, _devicePath is null
                ? "No Logi Bolt receiver HID++ endpoint was found."
                : $"The configured Logi Bolt receiver endpoint path '{_devicePath}' was not found.");
        }

        return receivers;
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
        var client = new HidppClient(new TracingHidTransaction(transaction, _log, receiver.DevicePath), receiver, _log);

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
                if (!await IdentifyAsync(client, receiver, slot, slotToken.Token).ConfigureAwait(false))
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

    /// <summary>Pings the slot and compares its identity with the profile. Returns false when another device lives there.</summary>
    private async Task<bool> IdentifyAsync(
        HidppClient client,
        HidCollectionInfo receiver,
        byte slot,
        CancellationToken cancellationToken)
    {
        using (var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            probe.CancelAfter(PingTimeout);
            try
            {
                await client.PingAsync(slot, probe.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline,
                    $"Logi Bolt slot 0x{slot:X2} did not answer its probe.");
            }
        }

        IReadOnlyList<ushort> modelIds = Array.Empty<ushort>();
        string modelText = "unavailable";
        try
        {
            var infoIndex = await client.ResolveFeatureAsync(slot, DeviceInformationFeature, cancellationToken).ConfigureAwait(false);
            var info = await client.SendAsync(slot, infoIndex, 0, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            modelIds = LogitechDeviceInformation.GetModelIds(info.Parameters).Select(entry => entry.ModelId).ToArray();
            modelText = LogitechDeviceInformation.FormatModelIds(info.Parameters);
        }
        catch (BatteryAdapterException exception) when (exception.Kind == BatteryAdapterErrorKind.UnsupportedCapability)
        {
        }

        var name = await LogitechDeviceName.ReadAsync(client, slot, cancellationToken).ConfigureAwait(false);
        var matches = _profile.Matches(name, modelIds);
        _log.Log(BatterioLogLevel.Information,
            $"Logi Bolt receiver '{receiver.DevicePath}' slot 0x{slot:X2}: name '{name ?? "unavailable"}', {modelText}; " +
            (matches ? $"matches {_profile.DisplayName}." : $"does not match {_profile.DisplayName}."));
        return matches;
    }

    private async Task<BatteryReading> ReadBatteryAsync(
        HidppClient client,
        HidCollectionInfo receiver,
        byte slot,
        CancellationToken cancellationToken)
    {
        BatteryReading reading;
        string featureName;
        string raw;
        byte? unifiedIndex = null;
        try
        {
            unifiedIndex = await client.ResolveFeatureAsync(slot, UnifiedBatteryFeature, cancellationToken).ConfigureAwait(false);
        }
        catch (BatteryAdapterException exception) when (exception.Kind == BatteryAdapterErrorKind.UnsupportedCapability)
        {
        }

        if (unifiedIndex is byte batteryIndex)
        {
            var capabilities = await client.SendAsync(slot, batteryIndex, 0, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            var status = await client.SendAsync(slot, batteryIndex, 0x10, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            reading = LogitechBatteryDecoding.DecodeUnifiedBattery(status.Parameters,
                LogitechBatteryDecoding.SupportsPercentage(capabilities.Parameters));
            featureName = "Unified Battery 0x1004";
            raw = Convert.ToHexString(status.Parameters.AsSpan(0, Math.Min(4, status.Parameters.Length)));
        }
        else
        {
            var legacyIndex = await client.ResolveFeatureAsync(slot, BatteryStatusFeature, cancellationToken).ConfigureAwait(false);
            var status = await client.SendAsync(slot, legacyIndex, 0, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            reading = LogitechBatteryDecoding.DecodeBatteryStatus(status.Parameters);
            featureName = "Battery Status 0x1000";
            raw = Convert.ToHexString(status.Parameters.AsSpan(0, Math.Min(3, status.Parameters.Length)));
        }

        var percentageText = reading.Percentage is int percentage ? $"{percentage}%" : "unknown";
        _log.Log(BatterioLogLevel.Information,
            $"{_profile.DisplayName} on '{receiver.DevicePath}' slot 0x{slot:X2} returned {percentageText} ({reading.ChargeState}) via {featureName}; raw status bytes {raw}.");
        return reading;
    }

    private sealed record PreferredSlot(string Path, byte Slot);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _lifetime.Cancel();
        return ValueTask.CompletedTask;
    }
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

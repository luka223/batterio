using System.Diagnostics;
using System.Text.Json;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

public sealed class LogitechGProX2SuperstrikeAdapter : IBatteryAdapter
{
    public const ushort VendorId = 0x046D;
    public const ushort ReceiverProductId = 0xC54D;
    public const ushort WiredProductId = 0xC0A8;
    public const ushort ProductId = ReceiverProductId;
    public const ushort InterfaceNumber = 2;
    public const ushort ExpectedWirelessModelId = 0x40BD;
    public const ushort ExpectedUsbModelId = 0xC0A8;

    private const byte EQuadTransportFlag = 0x04;
    private const byte UsbTransportFlag = 0x08;
    private static readonly EndpointMode WiredMode = new(
        "wired USB", LogitechReceiverDefinitions.SuperstrikeWiredQuery, false,
        UsbTransportFlag, ExpectedUsbModelId);
    private static readonly EndpointMode ReceiverMode = new(
        "receiver", LogitechReceiverDefinitions.SuperstrikeReceiverQuery, true,
        EQuadTransportFlag, ExpectedWirelessModelId);

    private readonly IHidCollectionProvider _provider;
    private readonly IBatterioLog _log;
    private readonly string? _devicePath;
    private int _disposed;
    private readonly CancellationTokenSource _lifetime = new();

    public LogitechGProX2SuperstrikeAdapter()
        : this(new EmptyHidCollectionProvider(), null, null)
    {
    }

    public LogitechGProX2SuperstrikeAdapter(
        IHidCollectionProvider provider,
        IBatterioLog? log = null,
        string? devicePath = null)
    {
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
            _log.Log(BatterioLogLevel.Warning, $"Logitech query failed ({error.Kind}): {error.Message}");
            throw;
        }
        finally
        {
            _log.Log(BatterioLogLevel.Information, $"Logitech query finished in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
        }
    }

    private async Task<BatteryReading> QueryAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();

        var endpoint = SelectEndpoint();
        var collection = endpoint.Collection;
        await using var protocolGate = await LogitechProtocolGate.EnterAsync(collection.DevicePath, cancellationToken).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await using var transport = await _provider.OpenAsync(collection, cancellationToken).ConfigureAwait(false);
        await using var transaction = await transport.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var client = new HidppClient(new TracingHidTransaction(transaction, _log, collection.DevicePath), collection, _log);

        if (!endpoint.Mode.IsReceiver)
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probe.CancelAfter(TimeSpan.FromMilliseconds(750));
            try
            {
                await client.PingAsync(0xFF, probe.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline,
                    "The wired Logitech Superstrike did not answer its HID++ probe at device index 0xFF.");
            }

            return await ReadBatteryAsync(client, endpoint.Mode, collection, 0xFF, started, cancellationToken).ConfigureAwait(false);
        }

        return await ReadReceiverBatteryAsync(client, endpoint.Mode, collection, started, cancellationToken).ConfigureAwait(false);
    }

    private Endpoint SelectEndpoint()
    {
        if (_devicePath is not null)
        {
            return SelectExplicitEndpoint();
        }

        var wired = FindCollections(WiredMode);
        if (wired.Length > 1)
        {
            throw Ambiguous(WiredMode);
        }

        if (wired.Length == 1)
        {
            return new Endpoint(WiredMode, wired[0]);
        }

        var receiver = FindCollections(ReceiverMode);
        if (receiver.Length > 1)
        {
            throw Ambiguous(ReceiverMode);
        }

        if (receiver.Length == 1)
        {
            return new Endpoint(ReceiverMode, receiver[0]);
        }

        throw new BatteryAdapterException(BatteryAdapterErrorKind.ReceiverMissing,
            "No supported Logitech Superstrike wired USB or receiver HID++ endpoint was found.");
    }

    private Endpoint SelectExplicitEndpoint()
    {
        var matches = new List<Endpoint>();
        Exception? enumerationFailure = null;
        foreach (var mode in new[] { WiredMode, ReceiverMode })
        {
            try
            {
                matches.AddRange(FindCollections(mode)
                    .Where(collection => string.Equals(collection.DevicePath, _devicePath, StringComparison.OrdinalIgnoreCase))
                    .Select(collection => new Endpoint(mode, collection)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                enumerationFailure ??= exception;
            }
        }

        if (matches.Count > 1)
        {
            var modeName = matches[0].Mode.Name;
            throw new BatteryAdapterException(BatteryAdapterErrorKind.ReceiverAmbiguous,
                $"More than one Logitech Superstrike HID++ collection matched configured devicePath '{_devicePath}' (first match was {modeName}).");
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (enumerationFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(enumerationFailure).Throw();
        }

        throw new BatteryAdapterException(BatteryAdapterErrorKind.ReceiverMissing,
            $"The configured Logitech Superstrike endpoint path '{_devicePath}' was not found.");
    }

    private HidCollectionInfo[] FindCollections(EndpointMode mode) =>
        (_provider.Enumerate(mode.Query) ?? Array.Empty<HidCollectionInfo>())
        .Where(mode.Query.Matches)
        .ToArray();

    private static BatteryAdapterException Ambiguous(EndpointMode mode) =>
        new(BatteryAdapterErrorKind.ReceiverAmbiguous,
            $"More than one Logitech Superstrike {mode.Name} HID++ collection matched; configure devicePath to select one.");

    private async Task<BatteryReading> ReadReceiverBatteryAsync(
        HidppClient client,
        EndpointMode mode,
        HidCollectionInfo collection,
        long started,
        CancellationToken cancellationToken)
    {
        BatteryAdapterException? lastFailure = null;
        foreach (var deviceIndex in Enumerable.Range(1, 6).Select(value => (byte)value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probe.CancelAfter(TimeSpan.FromMilliseconds(750));
                try
                {
                    await client.PingAsync(deviceIndex, probe.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    lastFailure = new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline,
                        $"HID++ receiver slot 0x{deviceIndex:X2} did not answer its probe.");
                    continue;
                }

                return await ReadBatteryAsync(client, mode, collection, deviceIndex, started, cancellationToken).ConfigureAwait(false);
            }
            catch (BatteryAdapterException exception) when (exception.Kind is BatteryAdapterErrorKind.UnsupportedCapability or BatteryAdapterErrorKind.DeviceOffline)
            {
                lastFailure = exception;
                _log.Log(BatterioLogLevel.Trace,
                    $"Superstrike receiver slot 0x{deviceIndex:X2} on '{collection.DevicePath}' was unavailable or had an unsupported identity: {exception.Message}");
            }
        }

        throw lastFailure ?? new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline,
            "No paired PRO X2 Superstrike device responded through the receiver.");
    }

    private async Task<BatteryReading> ReadBatteryAsync(
        HidppClient client,
        EndpointMode mode,
        HidCollectionInfo collection,
        byte deviceIndex,
        long started,
        CancellationToken cancellationToken)
    {
        var deviceInfoIndex = await client.ResolveFeatureAsync(deviceIndex, 0x0003, cancellationToken).ConfigureAwait(false);
        var deviceInfo = await client.SendAsync(deviceIndex, deviceInfoIndex, 0, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        if (!LogitechDeviceInformation.TryGetModelIdForTransport(
                deviceInfo.Parameters, mode.ExpectedTransportFlag, out var modelId) ||
            modelId != mode.ExpectedModelId)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.UnsupportedCapability,
                $"HID++ {mode.Name} endpoint '{collection.DevicePath}' at device index 0x{deviceIndex:X2} did not report the expected model ID 0x{mode.ExpectedModelId:X4}.");
        }

        _log.Log(BatterioLogLevel.Information,
            $"HID++ {mode.Name} endpoint '{collection.DevicePath}' at device index 0x{deviceIndex:X2} identified as model 0x{modelId:X4}.");

        var batteryIndex = await client.ResolveFeatureAsync(deviceIndex, 0x1004, cancellationToken).ConfigureAwait(false);
        var capabilities = await client.SendAsync(deviceIndex, batteryIndex, 0, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        _log.Log(BatterioLogLevel.Trace, "Superstrike Unified Battery capabilities query succeeded.");
        var status = await client.SendAsync(deviceIndex, batteryIndex, 0x10, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        var reading = LogitechBatteryDecoding.DecodeSuperstrike(status.Parameters,
            LogitechBatteryDecoding.SupportsPercentage(capabilities.Parameters));
        var rawStatus = status.Parameters.Length >= 3 ? $"0x{status.Parameters[2]:X2}" : "missing";
        var rawExternalPowerStatus = status.Parameters.Length >= 4 ? $"0x{status.Parameters[3]:X2}" : "missing";
        var percentageText = reading.Percentage is int percentage ? $"{percentage}%" : "unknown";
        _log.Log(BatterioLogLevel.Information,
            $"Superstrike {mode.Name} battery query on '{collection.DevicePath}' at device index 0x{deviceIndex:X2} returned {percentageText} with raw charging status {rawStatus} and external power status {rawExternalPowerStatus} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
        if (status.Parameters.Length >= 3 && !LogitechBatteryDecoding.IsRecognizedSuperstrikeStatus(status.Parameters[2]))
        {
            _log.Log(BatterioLogLevel.Warning, $"Unrecognized Superstrike charging status {rawStatus}.");
        }
        else if (status.Parameters.Length >= 3 && LogitechBatteryDecoding.IsSuperstrikeErrorStatus(status.Parameters[2]))
        {
            _log.Log(BatterioLogLevel.Warning, $"Superstrike battery reported an error charging status {rawStatus}.");
        }

        return reading;
    }

    private sealed record EndpointMode(
        string Name,
        HidCollectionQuery Query,
        bool IsReceiver,
        byte ExpectedTransportFlag,
        ushort ExpectedModelId);

    private sealed record Endpoint(EndpointMode Mode, HidCollectionInfo Collection);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _lifetime.Cancel();
        return ValueTask.CompletedTask;
    }
}

public sealed class LogitechGProX2SuperstrikeAdapterFactory : IBatteryAdapterFactory
{
    private readonly IHidCollectionProvider _provider;
    private readonly IBatterioLog _log;

    public LogitechGProX2SuperstrikeAdapterFactory()
        : this(new EmptyHidCollectionProvider(), null)
    {
    }

    public LogitechGProX2SuperstrikeAdapterFactory(IHidCollectionProvider provider, IBatterioLog? log = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _log = log ?? new NullBatterioLog();
    }

    public string AdapterId => "logitech-g-pro-x2-superstrike";

    public void ValidateSettings(JsonElement settings) => HidAdapterSettings.ReadDevicePath(settings, AdapterId);

    public IBatteryAdapter CreateAdapter(DeviceConfiguration device) =>
        new LogitechGProX2SuperstrikeAdapter(
            _provider,
            _log,
            HidAdapterSettings.ReadDevicePath(device.AdapterSettings, AdapterId));
}

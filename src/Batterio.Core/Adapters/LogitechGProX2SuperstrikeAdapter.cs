using System.Diagnostics;
using System.Text.Json;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

public sealed class LogitechGProX2SuperstrikeAdapter : IBatteryAdapter
{
    public const ushort VendorId = 0x046D;
    public const ushort ProductId = 0xC54D;
    public const ushort InterfaceNumber = 2;
    public const ushort ExpectedWirelessModelId = 0x40BD;

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

        var query = LogitechReceiverDefinitions.SuperstrikeQuery;
        var collections = _provider.Enumerate(query)
            .Where(query.Matches)
            .Where(collection => _devicePath is null ||
                                 string.Equals(collection.DevicePath, _devicePath, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (collections.Length == 0)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.ReceiverMissing,
                _devicePath is null
                    ? "The Logitech Superstrike receiver was not found."
                    : $"The configured Logitech Superstrike receiver path '{_devicePath}' was not found.");
        }

        if (_devicePath is null && collections.Length > 1)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.ReceiverAmbiguous,
                "More than one Logitech Superstrike receiver collection matched; configure devicePath to select one.");
        }

        var collection = collections[0];
        await using var protocolGate = await LogitechProtocolGate.EnterAsync(collection.DevicePath, cancellationToken).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await using var transport = await _provider.OpenAsync(collection, cancellationToken).ConfigureAwait(false);
        await using var transaction = await transport.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var client = new HidppClient(new TracingHidTransaction(transaction, _log, collection.DevicePath), collection, _log);
        BatteryAdapterException? lastFailure = null;

        foreach (var deviceIndex in Enumerable.Range(1, 6).Select(value => (byte)value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probe.CancelAfter(TimeSpan.FromMilliseconds(750));
                try { await client.PingAsync(deviceIndex, probe.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    lastFailure = new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline, $"HID++ slot {deviceIndex} did not answer its probe.");
                    continue;
                }
                var deviceInfoIndex = await client.ResolveFeatureAsync(deviceIndex, 0x0003, cancellationToken).ConfigureAwait(false);
                var batteryIndex = await client.ResolveFeatureAsync(deviceIndex, 0x1004, cancellationToken).ConfigureAwait(false);

                var deviceInfo = await client.SendAsync(deviceIndex, deviceInfoIndex, 0, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
                var modelOffset = deviceInfo.Parameters.Length >= 7
                    ? 7 + 2 * System.Numerics.BitOperations.PopCount((uint)(deviceInfo.Parameters[6] & 0x03)) : 7;
                var modelId = deviceInfo.Parameters.Length >= modelOffset + 2
                    ? (deviceInfo.Parameters[modelOffset] << 8 | deviceInfo.Parameters[modelOffset + 1]) : 0;
                if (deviceInfo.Parameters.Length < 9 ||
                    (deviceInfo.Parameters[6] & 0x04) == 0 ||
                    modelId != ExpectedWirelessModelId)
                {
                    lastFailure = new BatteryAdapterException(BatteryAdapterErrorKind.UnsupportedCapability,
                        $"HID++ slot 0x{deviceIndex:X2} is not the PRO X2 Superstrike (model ID was not 0x{ExpectedWirelessModelId:X4}).");
                    continue;
                }

                _log.Log(BatterioLogLevel.Information,
                    $"HID++ slot 0x{deviceIndex:X2} identified as wireless model 0x{modelId:X4}.");

                var capabilities = await client.SendAsync(deviceIndex, batteryIndex, 0, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
                _log.Log(BatterioLogLevel.Trace, "Superstrike Unified Battery capabilities query succeeded.");
                var status = await client.SendAsync(deviceIndex, batteryIndex, 0x10, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
                var reading = LogitechBatteryDecoding.DecodeSuperstrike(status.Parameters, LogitechBatteryDecoding.SupportsPercentage(capabilities.Parameters));
                var rawStatus = status.Parameters.Length >= 3 ? $"0x{status.Parameters[2]:X2}" : "missing";
                var percentageText = reading.Percentage is int percentage ? $"{percentage}%" : "unknown";
                _log.Log(BatterioLogLevel.Information,
                    $"Superstrike battery query on '{collection.DevicePath}' slot 0x{deviceIndex:X2} returned {percentageText} with raw status {rawStatus} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
                if (status.Parameters.Length >= 3 && !LogitechBatteryDecoding.IsRecognizedSuperstrikeStatus(status.Parameters[2]))
                {
                    _log.Log(BatterioLogLevel.Warning, $"Unrecognized Superstrike charging status {rawStatus}.");
                }
                else if (status.Parameters.Length >= 3 && LogitechBatteryDecoding.IsSuperstrikeErrorStatus(status.Parameters[2]))
                {
                    _log.Log(BatterioLogLevel.Warning, $"Superstrike battery reported an error status {rawStatus}.");
                }

                return reading;
            }
            catch (BatteryAdapterException exception) when (exception.Kind is BatteryAdapterErrorKind.UnsupportedCapability or BatteryAdapterErrorKind.DeviceOffline)
            {
                lastFailure = exception;
                _log.Log(BatterioLogLevel.Trace, $"HID++ slot 0x{deviceIndex:X2} did not identify as Superstrike: {exception.Message}");
            }
        }

        throw lastFailure ?? new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline,
            "No paired PRO X2 Superstrike device responded through the receiver.");
    }

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

    public void ValidateSettings(JsonElement settings) => LogitechAdapterSettings.ReadDevicePath(settings, AdapterId);

    public IBatteryAdapter CreateAdapter(DeviceConfiguration device) =>
        new LogitechGProX2SuperstrikeAdapter(
            _provider,
            _log,
            LogitechAdapterSettings.ReadDevicePath(device.AdapterSettings, AdapterId));
}

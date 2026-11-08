using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Reads a Logitech device that is connected directly over Bluetooth LE: open its HID++ collection, ping device index 0xFF,
/// then read the battery. No identity check is made: the product ID in the HID path already is the device.
/// </summary>
/// <remarks>
/// Over Bluetooth the HID++ channel is the vendor collection 0xFF43/0x0202 (long reports 0x11) and the device answers on
/// device index 0xFF, as in Solaar's direct Bluetooth support. Windows keeps the collection of a paired device that is asleep or
/// switched to another host, so a request that gets no answer (or fails because the link is gone) means "offline", not a fault.
/// </remarks>
public sealed class HidppBluetoothRoute : IBatteryRoute
{
    public const ushort LogitechVendorId = 0x046D;

    /// <summary>HID++ device index of a device that is addressed directly rather than through a receiver.</summary>
    public const byte DirectDeviceIndex = 0xFF;

    private readonly TimeSpan _pingTimeout;
    private readonly TimeSpan _readTimeout;

    private readonly IHidCollectionProvider _provider;
    private readonly IBatterioLog _log;
    private readonly string _displayName;

    public HidppBluetoothRoute(
        string displayName,
        IHidCollectionProvider provider,
        IBatterioLog? log = null,
        TimeSpan? pingTimeout = null,
        TimeSpan? readTimeout = null)
    {
        _displayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _log = log ?? new NullBatterioLog();
        _pingTimeout = pingTimeout ?? TimeSpan.FromSeconds(2);
        _readTimeout = readTimeout ?? TimeSpan.FromSeconds(3);
    }

    /// <summary>
    /// The HID++ collection of a Logitech device connected directly over Bluetooth LE
    /// (shown by Windows as "Logitech Download Assistant"): vendor usage page 0xFF43, usage 0x0202, long reports only.
    /// </summary>
    public static HidCollectionQuery Query(ushort productId) => new()
    {
        VendorId = LogitechVendorId,
        ProductId = productId,
        UsagePage = 0xFF43,
        Usage = 0x0202,
        MinimumReportLength = 20,
        RequiredReportId = 0x11
    };

    public async Task<BatteryReading> ReadAsync(ResolvedRoute route, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        var endpoint = route.Collection ?? throw new BatteryAdapterException(BatteryAdapterErrorKind.ReceiverMissing,
            $"Bluetooth route '{route.EndpointPath}' has no HID collection.");

        await using var protocolGate = await LogitechProtocolGate.EnterAsync(endpoint.DevicePath, cancellationToken).ConfigureAwait(false);
        await using var transport = await _provider.OpenAsync(endpoint, cancellationToken).ConfigureAwait(false);
        await using var transaction = await transport.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var client = new HidppClient(new TracingHidTransaction(transaction, _log, endpoint.DevicePath), endpoint, _log);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_readTimeout);
        try
        {
            using (var probe = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            {
                probe.CancelAfter(_pingTimeout);
                await client.PingAsync(DirectDeviceIndex, probe.Token).ConfigureAwait(false);
            }

            return await HidppBatteryReader.ReadAsync(
                client, DirectDeviceIndex, _displayName, endpoint.DevicePath, "over Bluetooth", _log, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline,
                $"{_displayName} did not answer in time over Bluetooth endpoint '{endpoint.DevicePath}'; it may be asleep or switched to another host.");
        }
        catch (IOException exception)
        {
            // The collection of a device that is not connected rejects writes and reads.
            throw new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline,
                $"{_displayName} is not connected on Bluetooth endpoint '{endpoint.DevicePath}'.", exception);
        }
    }
}

using System.Diagnostics;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Reads the Superstrike while it is plugged in with its USB cable: the mouse itself exposes the HID++ collection and answers on
/// device index 0xFF.
/// </summary>
public sealed class SuperstrikeCableRoute : IBatteryRoute
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(750);

    private readonly IHidCollectionProvider _provider;
    private readonly IBatterioLog _log;

    public SuperstrikeCableRoute(IHidCollectionProvider provider, IBatterioLog? log = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _log = log ?? new NullBatterioLog();
    }

    public async Task<BatteryReading> ReadAsync(ResolvedRoute route, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        var collection = route.Collection ?? throw new BatteryAdapterException(BatteryAdapterErrorKind.ReceiverMissing,
            $"USB cable route '{route.EndpointPath}' has no HID collection.");

        await using var protocolGate = await LogitechProtocolGate.EnterAsync(collection.DevicePath, cancellationToken).ConfigureAwait(false);
        await using var transport = await _provider.OpenAsync(collection, cancellationToken).ConfigureAwait(false);
        await using var transaction = await transport.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var client = new HidppClient(new TracingHidTransaction(transaction, _log, collection.DevicePath), collection, _log);

        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(ProbeTimeout);
        try
        {
            await client.PingAsync(LogitechGProX2Superstrike.CableDeviceIndex, probe.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline,
                "The wired Logitech Superstrike did not answer its HID++ probe at device index 0xFF.");
        }

        return await SuperstrikeBatteryReader.ReadAsync(
            client, "wired USB", collection.DevicePath, LogitechGProX2Superstrike.CableDeviceIndex,
            LogitechGProX2Superstrike.UsbTransportFlag, LogitechGProX2Superstrike.ExpectedUsbModelId,
            started, _log, cancellationToken).ConfigureAwait(false);
    }
}

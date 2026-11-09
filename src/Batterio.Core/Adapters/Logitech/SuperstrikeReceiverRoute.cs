using System.Diagnostics;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Reads the Superstrike through its Lightspeed receiver: the receiver has up to six pairing slots, so each is pinged in turn
/// (750 ms each) until the mouse answers on one.
/// </summary>
public sealed class SuperstrikeReceiverRoute : IBatteryRoute
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(750);

    private readonly IHidCollectionProvider _provider;
    private readonly IBatterioLog _log;

    public SuperstrikeReceiverRoute(IHidCollectionProvider provider, IBatterioLog? log = null)
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
            $"USB receiver route '{route.EndpointPath}' has no HID collection.");

        await using var protocolGate = await LogitechProtocolGate.EnterAsync(collection.DevicePath, cancellationToken).ConfigureAwait(false);
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
                probe.CancelAfter(ProbeTimeout);
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

                return await SuperstrikeBatteryReader.ReadAsync(
                    client, "receiver", collection.DevicePath, deviceIndex,
                    LogitechGProX2Superstrike.EQuadTransportFlag, LogitechGProX2Superstrike.ExpectedWirelessModelId,
                    started, _log, cancellationToken).ConfigureAwait(false);
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
}

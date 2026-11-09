using System.Diagnostics;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Reads the G Pro X2 Lightspeed headset through its receiver with the Centurion protocol: the modern battery request first, and the
/// narrow legacy request only when the modern one is positively rejected as unsupported.
/// </summary>
public sealed class LightspeedCenturionRoute : IBatteryRoute
{
    private readonly IHidCollectionProvider _provider;
    private readonly IBatterioLog _log;

    public LightspeedCenturionRoute(IHidCollectionProvider provider, IBatterioLog? log = null)
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
        var modern = new CenturionClient(new TracingHidTransaction(transaction, _log, collection.DevicePath), collection, _log);
        try
        {
            var reading = await modern.GetBatteryAsync(cancellationToken).ConfigureAwait(false);
            _log.Log(BatterioLogLevel.Information,
                $"LIGHTSPEED headset battery query on '{collection.DevicePath}' completed in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
            return reading;
        }
        catch (BatteryAdapterException exception) when (exception.Kind == BatteryAdapterErrorKind.UnsupportedCapability)
        {
            _log.Log(BatterioLogLevel.Information,
                $"Modern Centurion battery support was positively rejected; trying the narrow legacy battery request on '{collection.DevicePath}'.");
            return await modern.GetLegacyBatteryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

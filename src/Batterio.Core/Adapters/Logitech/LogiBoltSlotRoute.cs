using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Reads a device paired to a Logi Bolt receiver with one direct slot read. The receiver's pairing table already says whether the
/// device is the expected one (notices carry its WPID; a scan identified it once), so no identity check is repeated here.
/// A slot that the table reports offline costs nothing: no request is sent.
/// </summary>
public sealed class LogiBoltSlotRoute : IBatteryRoute
{
    private readonly LogiBoltReceiverManager _receivers;
    private readonly string _displayName;
    private readonly IBatterioLog _log;

    public LogiBoltSlotRoute(string displayName, LogiBoltReceiverManager receivers, IBatterioLog? log = null)
    {
        _displayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        _receivers = receivers ?? throw new ArgumentNullException(nameof(receivers));
        _log = log ?? new NullBatterioLog();
    }

    public Task<BatteryReading> ReadAsync(ResolvedRoute route, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.Slot is not byte slot)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.ProtocolError, $"Logi Bolt route '{route.EndpointPath}' has no slot.");
        }

        if (!_receivers.TryGetReceiver(route.EndpointPath, out var receiver))
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.ReceiverMissing,
                $"Logi Bolt receiver '{route.EndpointPath}' is not open.");
        }

        var entry = receiver.GetSlot(slot);
        if (entry is null || !entry.Online)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline,
                $"{_displayName} is not connected to Logi Bolt receiver '{route.EndpointPath}' (slot 0x{slot:X2}); it may be asleep or switched to another host.");
        }

        return receiver.ExecuteAsync(slot, (client, token) => HidppBatteryReader.ReadAsync(
            client, slot, _displayName, route.EndpointPath, $"slot 0x{slot:X2}", _log, token), cancellationToken);
    }
}

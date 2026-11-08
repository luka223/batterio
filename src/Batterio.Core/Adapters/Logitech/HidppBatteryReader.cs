using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Reads a HID++ 2.0 device's battery: Unified Battery (0x1004), falling back to Battery Status (0x1000).
/// Shared by every route, so there is one implementation and one set of decoding tests.
/// </summary>
public static class HidppBatteryReader
{
    private const ushort UnifiedBatteryFeature = 0x1004;
    private const ushort BatteryStatusFeature = 0x1000;

    /// <param name="where">Short description of the transport for the log, e.g. "over Bluetooth" or "slot 0x02".</param>
    public static async Task<BatteryReading> ReadAsync(
        HidppClient client,
        byte deviceIndex,
        string displayName,
        string endpointPath,
        string where,
        IBatterioLog log,
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
        log.Log(BatterioLogLevel.Information,
            $"{displayName} on '{endpointPath}' {where} returned {percentageText} ({reading.ChargeState}) via {featureName}; raw status bytes {raw}.");
        return reading;
    }
}

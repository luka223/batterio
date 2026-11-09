using System.Diagnostics;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Reads the G Pro X2 Superstrike's battery at one HID++ device index: checks the device's identity (Device Information 0x0003, the
/// model ID of the expected transport) and then reads Unified Battery 0x1004 with the Superstrike decoder.
/// Shared by the cable and the receiver route, which differ only in how they reach the device index.
/// </summary>
public static class SuperstrikeBatteryReader
{
    public static async Task<BatteryReading> ReadAsync(
        HidppClient client,
        string transportName,
        string endpointPath,
        byte deviceIndex,
        byte expectedTransportFlag,
        ushort expectedModelId,
        long started,
        IBatterioLog log,
        CancellationToken cancellationToken)
    {
        var deviceInfoIndex = await client.ResolveFeatureAsync(deviceIndex, 0x0003, cancellationToken).ConfigureAwait(false);
        var deviceInfo = await client.SendAsync(deviceIndex, deviceInfoIndex, 0, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        if (!LogitechDeviceInformation.TryGetModelIdForTransport(
                deviceInfo.Parameters, expectedTransportFlag, out var modelId) ||
            modelId != expectedModelId)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.UnsupportedCapability,
                $"HID++ {transportName} endpoint '{endpointPath}' at device index 0x{deviceIndex:X2} did not report the expected model ID 0x{expectedModelId:X4}.");
        }

        log.Log(BatterioLogLevel.Information,
            $"HID++ {transportName} endpoint '{endpointPath}' at device index 0x{deviceIndex:X2} identified as model 0x{modelId:X4}.");

        var batteryIndex = await client.ResolveFeatureAsync(deviceIndex, 0x1004, cancellationToken).ConfigureAwait(false);
        var capabilities = await client.SendAsync(deviceIndex, batteryIndex, 0, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        log.Log(BatterioLogLevel.Trace, "Superstrike Unified Battery capabilities query succeeded.");
        var status = await client.SendAsync(deviceIndex, batteryIndex, 0x10, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        var reading = LogitechBatteryDecoding.DecodeSuperstrike(status.Parameters,
            LogitechBatteryDecoding.SupportsPercentage(capabilities.Parameters));
        var rawStatus = status.Parameters.Length >= 3 ? $"0x{status.Parameters[2]:X2}" : "missing";
        var rawExternalPowerStatus = status.Parameters.Length >= 4 ? $"0x{status.Parameters[3]:X2}" : "missing";
        var percentageText = reading.Percentage is int percentage ? $"{percentage}%" : "unknown";
        log.Log(BatterioLogLevel.Information,
            $"Superstrike {transportName} battery query on '{endpointPath}' at device index 0x{deviceIndex:X2} returned {percentageText} with raw charging status {rawStatus} and external power status {rawExternalPowerStatus} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
        if (status.Parameters.Length >= 3 && !LogitechBatteryDecoding.IsRecognizedSuperstrikeStatus(status.Parameters[2]))
        {
            log.Log(BatterioLogLevel.Warning, $"Unrecognized Superstrike charging status {rawStatus}.");
        }
        else if (status.Parameters.Length >= 3 && LogitechBatteryDecoding.IsSuperstrikeErrorStatus(status.Parameters[2]))
        {
            log.Log(BatterioLogLevel.Warning, $"Superstrike battery reported an error charging status {rawStatus}.");
        }

        return reading;
    }
}

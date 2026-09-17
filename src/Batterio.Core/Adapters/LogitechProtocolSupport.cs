using System.Buffers.Binary;
using System.Text.Json;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

internal static class LogitechProtocolGate
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    public static async ValueTask<IAsyncDisposable> EnterAsync(string path, CancellationToken token)
    {
        Entry entry;
        lock (Sync)
        {
            if (!Entries.TryGetValue(path, out entry!)) Entries[path] = entry = new Entry();
            entry.Users++;
        }
        try { await entry.Gate.WaitAsync(token).ConfigureAwait(false); }
        catch { ReleaseReference(path, entry); throw; }
        return new Releaser(path, entry);
    }

    private static void ReleaseReference(string path, Entry entry)
    {
        lock (Sync)
        {
            if (--entry.Users == 0)
            {
                Entries.Remove(path);
                entry.Gate.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Users;
    }

    private sealed class Releaser(string path, Entry entry) : IAsyncDisposable
    {
        private int _disposed;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                entry.Gate.Release();
                ReleaseReference(path, entry);
            }
            return ValueTask.CompletedTask;
        }
    }
}

internal static class LogitechErrors
{
    public static BatteryAdapterException Feature(byte code, string protocol) => new(code switch
    {
        0x06 or 0x07 or 0x09 => BatteryAdapterErrorKind.UnsupportedCapability,
        0x08 => BatteryAdapterErrorKind.DeviceBusy,
        _ => BatteryAdapterErrorKind.ProtocolError
    }, $"{protocol} feature error 0x{code:X2}.");
}

internal static class LogitechDeviceInformation
{
    public static bool TryGetModelIdForTransport(
        IReadOnlyList<byte> parameters,
        byte transportFlag,
        out ushort modelId)
    {
        modelId = 0;
        if (parameters.Count < 7 || transportFlag == 0 || (transportFlag & (transportFlag - 1)) != 0)
        {
            return false;
        }

        var transportBitmap = parameters[6];
        if ((transportBitmap & transportFlag) == 0)
        {
            return false;
        }

        var precedingTransports = System.Numerics.BitOperations.PopCount(
            (uint)(transportBitmap & (transportFlag - 1)));
        var modelOffset = 7 + (2 * precedingTransports);
        if (parameters.Count < modelOffset + 2)
        {
            return false;
        }

        modelId = (ushort)((parameters[modelOffset] << 8) | parameters[modelOffset + 1]);
        return true;
    }
}

internal static class LogitechAdapterSettings
{
    public static string? ReadDevicePath(JsonElement settings, string adapterId)
    {
        if (settings.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        if (settings.ValueKind != JsonValueKind.Object)
        {
            throw new AdapterConfigurationException($"Adapter '{adapterId}' settings must be a JSON object.");
        }

        if (!settings.TryGetProperty("devicePath", out var path))
        {
            return null;
        }

        if (path.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(path.GetString()))
        {
            throw new AdapterConfigurationException($"Adapter '{adapterId}' devicePath must be a nonempty string when supplied.");
        }

        return path.GetString();
    }
}

public sealed record HidppMessage(
    byte DeviceIndex,
    byte FeatureIndex,
    byte Function,
    byte SoftwareId,
    byte[] Parameters);

public sealed class HidppClient
{
    private const byte ShortReportId = 0x10;
    private const byte LongReportId = 0x11;
    private const byte SoftwareId = 0x01;


    private readonly IHidReportTransaction _transaction;
    private readonly HidCollectionInfo _collection;
    private readonly IBatterioLog _log;

    public HidppClient(IHidReportTransaction transaction, HidCollectionInfo collection, IBatterioLog log)
    {
        _transaction = transaction;
        _collection = collection;
        _log = log;
    }

    public async Task<HidppMessage> SendAsync(
        byte deviceIndex,
        byte featureIndex,
        byte function,
        ReadOnlyMemory<byte> parameters,
        CancellationToken cancellationToken,
        byte? pingMarker = null)
    {
        var shortReport = parameters.Length <= 3 && _collection.SupportsReport(ShortReportId);
        var reportId = shortReport ? ShortReportId : LongReportId;
        var protocolLength = shortReport ? 7 : 20;
        if (parameters.Length > 16)
        {
            throw new BatteryAdapterException(
                BatteryAdapterErrorKind.ProtocolError,
                "HID++ request parameters exceed the long-report capacity.");
        }

        if (!_collection.SupportsReport(reportId))
        {
            throw new BatteryAdapterException(
                BatteryAdapterErrorKind.UnsupportedCapability,
                $"HID++ report 0x{reportId:X2} is not supported by '{_collection.DevicePath}'.");
        }

        var report = new byte[Math.Max(protocolLength, _collection.OutputReportLength)];
        report[0] = reportId;
        report[1] = deviceIndex;
        report[2] = featureIndex;
        report[3] = (byte)((function & 0xF0) | SoftwareId);
        parameters.Span.CopyTo(report.AsSpan(4));
        await _transaction.WriteAsync(report, cancellationToken).ConfigureAwait(false);

        var inputLength = Math.Max(20, _collection.InputReportLength);
        var input = new byte[inputLength];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await _transaction.ReadAsync(input, cancellationToken).ConfigureAwait(false);
            if (count <= 0)
            {
                throw new BatteryAdapterException(
                    BatteryAdapterErrorKind.DeviceOffline,
                    $"No HID++ response was received from '{_collection.DevicePath}'.");
            }

            if (count < 4 || (input[0] != ShortReportId && input[0] != LongReportId))
            {
                _log.Log(BatterioLogLevel.Trace, $"Ignored malformed or unrelated HID++ report from '{_collection.DevicePath}'.");
                continue;
            }

            if (input[1] != deviceIndex)
            {
                _log.Log(BatterioLogLevel.Trace, $"Ignored HID++ notification for device index 0x{input[1]:X2}.");
                continue;
            }

            if (input[2] is 0x8F or 0xFF)
            {
                if (count < 6)
                    throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse, "Truncated HID++ error.");
                if (input[3] != featureIndex || input[4] != report[3]) continue;
                if (count < (input[0] == ShortReportId ? 7 : 20))
                    throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse, "Truncated HID++ error report.");
                if (input[2] == 0xFF) throw LogitechErrors.Feature(input[5], "HID++");
                throw new BatteryAdapterException(input[5] switch
                {
                    0x04 or 0x08 => BatteryAdapterErrorKind.DeviceOffline,
                    0x07 => BatteryAdapterErrorKind.DeviceBusy,
                    0x01 or 0x02 => BatteryAdapterErrorKind.UnsupportedCapability,
                    _ => BatteryAdapterErrorKind.ProtocolError
                }, $"HID++ receiver error 0x{input[5]:X2} for slot 0x{deviceIndex:X2}.");
            }

            if (input[2] != featureIndex || (input[3] & 0xF0) != (function & 0xF0) ||
                (input[3] & 0x0F) != SoftwareId)
            {
                _log.Log(BatterioLogLevel.Trace, $"Ignored unrelated HID++ response from '{_collection.DevicePath}'.");
                continue;
            }

            var responseLength = input[0] == ShortReportId ? 7 : 20;
            if (count < responseLength)
            {
                throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse, "Truncated correlated HID++ report.");
            }

            if (pingMarker.HasValue && input[6] != pingMarker.Value) continue;

            return new HidppMessage(
                input[1],
                input[2],
                input[3],
                (byte)(input[3] & 0x0F),
                input.AsSpan(4, responseLength - 4).ToArray());
        }
    }

    public async Task<HidppMessage> PingAsync(byte deviceIndex, CancellationToken cancellationToken)
    {
        var marker = (byte)Random.Shared.Next(1, 256);
        var reply = await SendAsync(deviceIndex, 0, 0x10, new byte[] { 0, 0, marker }, cancellationToken, marker).ConfigureAwait(false);
        if (reply.Parameters[0] < 2)
            throw new BatteryAdapterException(BatteryAdapterErrorKind.UnsupportedCapability, "HID++ 2.0 or later is required.");
        _log.Log(BatterioLogLevel.Information, $"HID++ {reply.Parameters[0]}.{reply.Parameters[1]} on slot 0x{deviceIndex:X2}.");
        return reply;
    }

    public async Task<byte> ResolveFeatureAsync(byte deviceIndex, ushort featureId, CancellationToken cancellationToken)
    {
        var reply = await SendAsync(deviceIndex, 0, 0,
            new byte[] { (byte)(featureId >> 8), (byte)featureId }, cancellationToken).ConfigureAwait(false);
        var index = reply.Parameters[0];
        if (index == 0)
            throw new BatteryAdapterException(BatteryAdapterErrorKind.UnsupportedCapability, $"HID++ feature 0x{featureId:X4} is absent.");
        _log.Log(BatterioLogLevel.Information, $"HID++ feature 0x{featureId:X4} resolved to 0x{index:X2}.");
        return index;
    }

}

public static class LogitechBatteryDecoding
{
    private const byte SuperstrikeChargeCompleteStatus = 0x03;
    private const byte SuperstrikeExternalPowerConnectedStatus = 0x01;

    public static BatteryChargeState DecodeSuperstrikeStatus(byte status) => status switch
    {
        0x00 => BatteryChargeState.NotCharging,
        0x01 or 0x02 => BatteryChargeState.Charging,
        0x03 or 0x04 => BatteryChargeState.NotCharging,
        0x05 or 0x06 => BatteryChargeState.Offline,
        _ => BatteryChargeState.NotCharging
    };

    public static BatteryChargeState DecodeCenturionStatus(byte status) => status switch
    {
        0x00 => BatteryChargeState.NotCharging,
        0x01 or 0x02 => BatteryChargeState.Charging,
        0x03 => BatteryChargeState.NotCharging,
        _ => BatteryChargeState.NotCharging
    };

    public static bool IsRecognizedSuperstrikeStatus(byte status) => status <= 0x06;

    public static bool IsSuperstrikeErrorStatus(byte status) => status is 0x04 or 0x05 or 0x06;

    public static bool IsRecognizedCenturionStatus(byte status) => status <= 0x03;

    public static BatteryReading DecodeSuperstrike(IReadOnlyList<byte> parameters, bool supportsPercentage = true)
    {
        if (parameters.Count < 1)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse,
                "HID++ Unified Battery returned an invalid percentage.");
        }

        if (parameters.Count >= 3 && parameters[2] is 0x05 or 0x06)
        {
            return BatteryReading.Offline;
        }

        if (supportsPercentage && parameters[0] > 100)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse,
                "HID++ Unified Battery returned an invalid percentage.");
        }

        if (!supportsPercentage && parameters.Count < 3)
        {
            return BatteryReading.Offline;
        }

        if (!supportsPercentage && !IsRecognizedSuperstrikeStatus(parameters[2]))
        {
            return BatteryReading.Offline;
        }

        int? percentage = supportsPercentage ? parameters[0] : null;
        var chargeState = parameters.Count >= 3
            ? DecodeSuperstrikeStatus(parameters[2])
            : BatteryChargeState.NotCharging;
        if (chargeState == BatteryChargeState.NotCharging &&
            parameters.Count >= 4 &&
            parameters[2] == SuperstrikeChargeCompleteStatus &&
            parameters[3] == SuperstrikeExternalPowerConnectedStatus)
        {
            // A full battery reports "complete", but the external-power flag keeps the
            // charging indicator visible while the mouse remains connected over USB.
            chargeState = BatteryChargeState.Charging;
        }

        return new BatteryReading(percentage, chargeState);
    }

    public static bool SupportsPercentage(IReadOnlyList<byte> capabilities)
    {
        if (capabilities.Count < 2)
            throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse, "Truncated Unified Battery capabilities.");
        return (capabilities[1] & 0x02) != 0;
    }

    public static BatteryReading DecodeCenturion(IReadOnlyList<byte> payload)
    {
        if (payload.Count < 1 || payload[0] > 100)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse,
                "Centurion battery response returned an invalid percentage.");
        }

        var chargeState = payload.Count >= 3
            ? DecodeCenturionStatus(payload[2])
            : BatteryChargeState.NotCharging;
        return new BatteryReading(payload[0], chargeState);
    }
}

public sealed class CenturionClient
{
    private const byte ReportId = 0x51;
    private const byte SoftwareId = 0x01;
    private const byte BridgeSendFunction = 0x10;
    private const int FrameSize = 64;
    private const int MaxSingleBridgePayload = 56;

    private readonly IHidReportTransaction _transaction;
    private readonly HidCollectionInfo _collection;
    private readonly IBatterioLog _log;
    private byte? _bridgeIndex;
    private byte? _batteryIndex;

    public CenturionClient(IHidReportTransaction transaction, HidCollectionInfo collection, IBatterioLog log)
    {
        _transaction = transaction;
        _collection = collection;
        _log = log;
    }

    public async Task<BatteryReading> GetBatteryAsync(CancellationToken cancellationToken)
    {
        await DiscoverFeaturesAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendBridgeAsync(_batteryIndex!.Value, 0, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        var reading = LogitechBatteryDecoding.DecodeCenturion(response);
        var rawStatus = response.Length >= 3 ? $"0x{response[2]:X2}" : "missing";
        var percentageText = reading.Percentage is int percentage ? $"{percentage}%" : "unknown";
        _log.Log(BatterioLogLevel.Information,
            $"Centurion battery query on '{_collection.DevicePath}' returned {percentageText} with raw status {rawStatus}.");
        if (response.Length >= 3 && !LogitechBatteryDecoding.IsRecognizedCenturionStatus(response[2]))
        {
            _log.Log(BatterioLogLevel.Warning, $"Unrecognized Centurion charging status {rawStatus}.");
        }
        return reading;
    }

    public async Task DiscoverFeaturesAsync(CancellationToken cancellationToken)
    {
        if (_batteryIndex.HasValue)
        {
            return;
        }

        var featureSetLookup = await SendDirectAsync(0, 0, new byte[] { 0x00, 0x01 }, cancellationToken).ConfigureAwait(false);
        if (featureSetLookup.Length < 1)
            throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse, "Empty Centurion ROOT response.");
        if (featureSetLookup[0] == 0)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.UnsupportedCapability, "Centurion FeatureSet is not available.");
        }

        var featureSetIndex = featureSetLookup[0];
        var count = await SendDirectAsync(featureSetIndex, 0, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        if (count.Length < 1)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse, "Centurion FeatureSet count response was empty.");
        }

        for (byte index = 0; index < count[0]; index++)
        {
            var feature = await SendDirectAsync(featureSetIndex, 0x10, new[] { index }, cancellationToken).ConfigureAwait(false);
            if (feature.Length < 3)
            {
                throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse,
                    $"Centurion FeatureSet response for slot {index} was too short.");
            }

            var featureId = BinaryPrimitives.ReadUInt16BigEndian(feature.AsSpan(1, 2));
            if (featureId == 0x0003)
            {
                _bridgeIndex = index;
                break;
            }
        }

        if (!_bridgeIndex.HasValue)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.UnsupportedCapability, "Centurion bridge feature is not available.");
        }

        var subFeatureSetLookup = await SendBridgeAsync(0, 0, new byte[] { 0x00, 0x01 }, cancellationToken).ConfigureAwait(false);
        if (subFeatureSetLookup.Length < 1)
            throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse, "Empty Centurion subdevice ROOT response.");
        if (subFeatureSetLookup[0] == 0)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.UnsupportedCapability,
                "Centurion headset FeatureSet is not available.");
        }

        var subFeatureSetIndex = subFeatureSetLookup[0];
        var subCount = await SendBridgeAsync(subFeatureSetIndex, 0, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        if (subCount.Length < 1)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse,
                "Centurion headset FeatureSet count response was empty.");
        }

        for (byte index = 0; index < subCount[0]; index++)
        {
            var feature = await SendBridgeAsync(subFeatureSetIndex, 0x10, new[] { index }, cancellationToken).ConfigureAwait(false);
            if (feature.Length < 3)
            {
                throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse,
                    $"Centurion headset FeatureSet response for slot {index} was too short.");
            }

            var featureId = BinaryPrimitives.ReadUInt16BigEndian(feature.AsSpan(1, 2));
            if (featureId == 0x0104)
            {
                _batteryIndex = index;
            }
        }

        var batteryIndexText = _batteryIndex is byte resolvedBatteryIndex ? resolvedBatteryIndex.ToString("X2") : "missing";
        _log.Log(BatterioLogLevel.Information,
            $"Centurion feature discovery on '{_collection.DevicePath}' resolved bridge index 0x{_bridgeIndex.Value:X2} and battery index 0x{batteryIndexText}.");

        if (!_batteryIndex.HasValue)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.UnsupportedCapability,
                "Centurion headset battery feature 0x0104 is not available.");
        }
    }

    public async Task<BatteryReading> GetLegacyBatteryAsync(CancellationToken cancellationToken)
    {
        var request = new byte[Math.Max(FrameSize, _collection.OutputReportLength)];
        request[0] = ReportId;
        request[1] = 0x08;
        request[3] = 0x03;
        request[4] = 0x1A;
        request[6] = 0x03;
        request[8] = 0x04;
        request[9] = 0x0A;
        await _transaction.WriteAsync(request, cancellationToken).ConfigureAwait(false);

        var input = new byte[Math.Max(FrameSize, _collection.InputReportLength)];
        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await _transaction.ReadAsync(input, cancellationToken).ConfigureAwait(false);
            if (count < 2)
            {
                throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse,
                    "Legacy Centurion response was shorter than its header.");
            }

            if (input[0] != ReportId)
            {
                continue;
            }

            if (input[1] == 0x05)
            {
                if (count >= 7 && input[6] == 0x00)
                {
                    throw new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline,
                        "The Logitech headset is powered off or not connected.");
                }

                continue;
            }

            if (input[1] == 0x03)
            {
                continue;
            }

            if (count != FrameSize || input[2] != 0 || input[1] != 0x0B || input[8] != 0x04)
            {
                continue;
            }

            if (input[10] > 100)
            {
                throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse,
                    "Legacy Centurion battery percentage was outside 0-100.");
            }

            var chargeState = input[12] == 0x02
                ? BatteryChargeState.Charging
                : BatteryChargeState.NotCharging;
            _log.Log(BatterioLogLevel.Information,
                $"Legacy Centurion battery query on '{_collection.DevicePath}' returned {input[10]}% with raw status 0x{input[12]:X2}.");
            return new BatteryReading(input[10], chargeState);
        }

        throw new BatteryAdapterException(BatteryAdapterErrorKind.ProtocolError,
            "Legacy Centurion battery response was not received.");
    }

    public static byte[] BuildFrame(ReadOnlySpan<byte> payload, byte flags = 0)
    {
        if (payload.Length > FrameSize - 3)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Centurion payload exceeds a 64-byte frame.");
        }

        var frame = new byte[FrameSize];
        frame[0] = ReportId;
        frame[1] = checked((byte)(payload.Length + 1));
        frame[2] = flags;
        payload.CopyTo(frame.AsSpan(3));
        return frame;
    }

    public static bool TryExtractPayload(ReadOnlySpan<byte> frame, out byte[] payload)
    {
        payload = Array.Empty<byte>();
        if (frame.Length < FrameSize || frame[0] != ReportId || frame[2] != 0)
        {
            return false;
        }

        var completionLength = frame[1];
        if (completionLength <= 1 || completionLength + 2 > frame.Length)
        {
            return false;
        }

        payload = frame.Slice(3, completionLength - 1).ToArray();
        return true;
    }

    private async Task<byte[]> SendDirectAsync(
        byte featureIndex,
        byte function,
        ReadOnlyMemory<byte> parameters,
        CancellationToken cancellationToken)
    {
        var payload = new byte[2 + parameters.Length];
        payload[0] = featureIndex;
        payload[1] = (byte)((function & 0xF0) | SoftwareId);
        parameters.Span.CopyTo(payload.AsSpan(2));
        await _transaction.WriteAsync(BuildFrame(payload), cancellationToken).ConfigureAwait(false);

        var input = new byte[Math.Max(FrameSize, _collection.InputReportLength)];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await _transaction.ReadAsync(input, cancellationToken).ConfigureAwait(false);
            if (count <= 0)
            {
                throw new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline,
                    $"No Centurion response was received from '{_collection.DevicePath}'.");
            }

            if (input[0] != ReportId) continue;
            if (!TryExtractPayload(input.AsSpan(0, Math.Min(count, input.Length)), out var response) || response.Length < 2)
            {
                throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse,
                    "Centurion response frame was malformed.");
            }

            if (response[0] == featureIndex && response[1] == (byte)((function & 0xF0) | SoftwareId))
            {
                return response[2..];
            }

            if (response[0] == 0xFF)
            {
                if (response.Length < 4)
                    throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse, "Truncated Centurion error envelope.");
                if (response[1] != featureIndex || response[2] != payload[1]) continue;
                throw LogitechErrors.Feature(response[3], "Centurion");
            }

            _log.Log(BatterioLogLevel.Trace, $"Ignored unrelated Centurion response from '{_collection.DevicePath}'.");
        }
    }

    private async Task<byte[]> SendBridgeAsync(
        byte subFeatureIndex,
        byte function,
        ReadOnlyMemory<byte> parameters,
        CancellationToken cancellationToken)
    {
        if (!_bridgeIndex.HasValue)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.ProtocolError, "Centurion bridge index is not initialized.");
        }

        var subMessage = new byte[3 + parameters.Length];
        subMessage[0] = 0x00;
        subMessage[1] = subFeatureIndex;
        subMessage[2] = (byte)((function & 0xF0) | SoftwareId);
        parameters.Span.CopyTo(subMessage.AsSpan(3));
        if (subMessage.Length > 0x0FFF)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.ProtocolError,
                "Centurion bridge message exceeds its 12-bit size limit.");
        }

        var prefix = new byte[]
        {
            _bridgeIndex.Value,
            (byte)(BridgeSendFunction | SoftwareId),
            (byte)((subMessage.Length >> 8) & 0x0F),
            (byte)(subMessage.Length & 0xFF)
        };

        if (subMessage.Length > MaxSingleBridgePayload)
            throw new BatteryAdapterException(BatteryAdapterErrorKind.ProtocolError, "Fragmented bridge requests are not supported.");
        var layer = new byte[prefix.Length + subMessage.Length];
        prefix.CopyTo(layer, 0);
        subMessage.CopyTo(layer, prefix.Length);
        await _transaction.WriteAsync(BuildFrame(layer), cancellationToken).ConfigureAwait(false);

        var input = new byte[Math.Max(FrameSize, _collection.InputReportLength)];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await _transaction.ReadAsync(input, cancellationToken).ConfigureAwait(false);
            if (count <= 0)
            {
                throw new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline,
                    $"No Centurion bridge response was received from '{_collection.DevicePath}'.");
            }

            if (input[0] != ReportId) continue;
            if (!TryExtractPayload(input.AsSpan(0, count), out var response) || response.Length < 2)
                throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse, "Malformed or fragmented Centurion bridge frame.");
            if (response[0] == 0xFF)
            {
                if (response.Length < 4)
                    throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse, "Truncated bridge error.");
                if (response[1] != _bridgeIndex.Value || response[2] != prefix[1]) continue;
                throw LogitechErrors.Feature(response[3], "Centurion bridge");
            }
            if (response[0] != _bridgeIndex.Value || (response[1] & 0xF0) != 0x10) continue;
            if (response[1] == prefix[1]) continue; // ACK; only the nested response contains data.
            if ((response[1] & 0x0F) != 0) continue;
            if (response.Length < 7 || (response[2] & 0xF0) != 0 ||
                ((response[2] << 8) | response[3]) != response.Length - 4)
                throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse, "Invalid Centurion submessage length.");
            if (response[4] != 0)
                throw new BatteryAdapterException(BatteryAdapterErrorKind.ProtocolError, $"Centurion subdevice error 0x{response[4]:X2}.");
            if (response[5] == 0xFF)
            {
                if (response.Length < 9)
                    throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse, "Truncated Centurion subdevice error.");
                if (response[6] != subFeatureIndex || response[7] != subMessage[2]) continue;
                throw LogitechErrors.Feature(response[8], "Centurion subdevice");
            }
            if (response[5] != subFeatureIndex || response[6] != subMessage[2]) continue;
            return response[7..];
        }

    }
}

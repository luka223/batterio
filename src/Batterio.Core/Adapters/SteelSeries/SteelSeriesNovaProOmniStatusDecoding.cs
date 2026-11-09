using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>Pure decoder for the Arctis Nova Pro Omni hub status report (<c>01 B0</c>).</summary>
public static class SteelSeriesNovaProOmniStatusDecoding
{
    public const byte ReportId = 0x01;
    public const byte StatusOpcode = 0xB0;
    public const byte EventPrefix = 0x07;

    public const int HeadsetBatteryOffset = 0x06;
    public const int ReserveBatteryOffset = 0x07;
    public const int LinkOffset = 0x0E;
    public const int ChargingOffset = 0x0F;

    /// <summary>Reserve byte reported while the reserve slot holds no battery.</summary>
    public const byte ReserveEmpty = 0x00;

    public const byte LinkConnected = 0x08;
    public const byte ChargingActive = 0x02;
    public const byte ChargingPlugged = 0x04;

    /// <summary>Shortest frame that still contains every field the decoder reads.</summary>
    public const int MinimumReplyLength = 0x11;

    public static byte[] CreateStatusRequest(int outputReportLength)
    {
        if (outputReportLength < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(outputReportLength), outputReportLength,
                "The output report must have room for the report ID and opcode.");
        }

        var request = new byte[outputReportLength];
        request[0] = ReportId;
        request[1] = StatusOpcode;
        return request;
    }

    public static bool IsStatusReply(ReadOnlySpan<byte> frame) =>
        frame.Length >= MinimumReplyLength && frame[0] == ReportId && frame[1] == StatusOpcode;

    public static BatteryReading Decode(ReadOnlySpan<byte> frame)
    {
        if (!IsStatusReply(frame))
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse,
                "The Arctis Nova Pro Omni status report was too short or did not start with 01 B0.");
        }

        return DecodeHeadset(frame) with { Reserve = DecodeReserve(frame) };
    }

    private static BatteryReading DecodeHeadset(ReadOnlySpan<byte> frame)
    {
        if (frame[LinkOffset] != LinkConnected)
        {
            return BatteryReading.Offline;
        }

        var percentage = frame[HeadsetBatteryOffset];
        if (percentage > 100)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse,
                $"The Arctis Nova Pro Omni reported an impossible headset battery of {percentage}%.");
        }

        var charging = frame[ChargingOffset] == ChargingActive ||
                       (frame[ChargingOffset] == ChargingPlugged && percentage == 100);
        return new BatteryReading(percentage, charging ? BatteryChargeState.Charging : BatteryChargeState.NotCharging);
    }

    /// <summary>
    /// The hub has no reserve-charging flag, but it charges the slot whenever it is powered,
    /// so a reserve below 100% is shown as charging. An empty slot reports 0 (hardware capture; no other byte
    /// changes), so 0 means no reserve. A fully drained battery would also read 0, which is acceptable because
    /// it is charged straight away. Values above 100 are also treated as no reserve.
    /// </summary>
    private static BatteryReading? DecodeReserve(ReadOnlySpan<byte> frame)
    {
        var percentage = frame[ReserveBatteryOffset];
        return percentage == ReserveEmpty || percentage > 100
            ? null
            : new BatteryReading(percentage, percentage < 100 ? BatteryChargeState.Charging : BatteryChargeState.NotCharging);
    }
}

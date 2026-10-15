using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Pure decoder for the battery report the Q1 HE custom firmware pushes through the Keychron Link dongle.
/// Offsets include the Windows report-ID byte at index 0: <c>00 A4 pct mvLo mvHi charge connection model</c>.
/// </summary>
public static class KeychronBatteryReportDecoding
{
    public const int CommandOffset = 1;
    public const int PercentageOffset = 2;
    public const int MillivoltsOffset = 3;
    public const int ChargeOffset = 5;
    public const int ConnectionOffset = 6;
    public const int ModelOffset = 7;

    public const byte ChargeOnBattery = 0;
    public const byte ChargeCharging = 1;
    public const byte ChargeFull = 2;

    /// <summary>Shortest frame that still contains every field the decoder reads.</summary>
    public const int MinimumReportLength = 8;

    public static bool IsQ1HeBatteryReport(ReadOnlySpan<byte> frame) =>
        frame.Length >= MinimumReportLength &&
        frame[CommandOffset] == KeychronDefinitions.BatteryReportCommand &&
        frame[ModelOffset] == KeychronDefinitions.Q1HeModelId;

    public static ushort DecodeMillivolts(ReadOnlySpan<byte> frame)
    {
        RequireReport(frame);
        return (ushort)(frame[MillivoltsOffset] | (frame[MillivoltsOffset + 1] << 8));
    }

    public static BatteryReading Decode(ReadOnlySpan<byte> frame, IBatterioLog? log = null)
    {
        RequireReport(frame);

        var percentage = frame[PercentageOffset];
        if (percentage > 100)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse,
                $"The Keychron Q1 HE reported an impossible battery of {percentage}%.");
        }

        var charge = frame[ChargeOffset];
        var state = charge switch
        {
            ChargeCharging => BatteryChargeState.Charging,
            ChargeOnBattery or ChargeFull => BatteryChargeState.NotCharging,
            _ => BatteryChargeState.NotCharging
        };
        if (charge > ChargeFull)
        {
            log?.Log(BatterioLogLevel.Warning, $"Keychron Q1 HE reported unknown charge byte 0x{charge:X2}.");
        }

        return new BatteryReading(percentage, state);
    }

    private static void RequireReport(ReadOnlySpan<byte> frame)
    {
        if (!IsQ1HeBatteryReport(frame))
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.MalformedResponse,
                "The Keychron Q1 HE battery report was too short or did not start with A4 and model 2.");
        }
    }
}

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

    // The firmware maps voltage straight to percent, so the charger's voltage lift shows up as a jump on plug-in.
    // Calibrated on a full charge logged 2026-11-04: ~200 mV lift while the charger pushes constant current,
    // shrinking to ~46 mV at 4147 mV as the current tapers (a full, idle battery reads 4101 mV).
    public const int ChargingLiftMillivolts = 200;
    public const int ChargingTaperStartMillivolts = 4100;
    public const int ChargingTaperEndMillivolts = 4147;
    public const int ChargingTaperEndLiftMillivolts = 46;

    /// <summary>Charging never shows 100% until the keyboard reports the full charge byte.</summary>
    public const double MaximumChargingPercentage = 99;

    /// <summary>The fastest the charger was observed to fill the battery (21% to the taper knee in ~140 minutes).</summary>
    public const double MaximumChargingRisePerMinute = 0.4;

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

        if (state == BatteryChargeState.Charging)
        {
            return new BatteryReading((int)EstimateChargingPercentage(DecodeMillivolts(frame)), state);
        }

        return new BatteryReading(percentage, state);
    }

    /// <summary>The firmware's own voltage-to-percent line: 3300 mV = 0%, 3500 mV = 20%, 4100 mV = 100%.</summary>
    public static double PercentageFromMillivolts(double millivolts) => millivolts switch
    {
        <= 3300 => 0,
        <= 3500 => (millivolts - 3300) / 10,
        >= 4100 => 100,
        _ => 20 + (millivolts - 3500) * 80 / 600
    };

    /// <summary>How far the charger lifts the reported voltage above the battery's own.</summary>
    public static double ChargingLift(ushort millivolts)
    {
        if (millivolts <= ChargingTaperStartMillivolts) return ChargingLiftMillivolts;
        if (millivolts >= ChargingTaperEndMillivolts) return ChargingTaperEndLiftMillivolts;
        var progress = (double)(millivolts - ChargingTaperStartMillivolts) / (ChargingTaperEndMillivolts - ChargingTaperStartMillivolts);
        return ChargingLiftMillivolts - progress * (ChargingLiftMillivolts - ChargingTaperEndLiftMillivolts);
    }

    /// <summary>Battery percentage while charging, from the reported voltage alone.</summary>
    public static double EstimateChargingPercentage(ushort millivolts) =>
        Math.Min(MaximumChargingPercentage, PercentageFromMillivolts(millivolts - ChargingLift(millivolts)));

    /// <summary>
    /// Keeps a charging estimate from falling or from rising faster than the charger can fill the battery.
    /// </summary>
    public static double LimitChargingRise(double estimate, double previous, TimeSpan elapsed)
    {
        var ceiling = previous + MaximumChargingRisePerMinute * Math.Max(0, elapsed.TotalMinutes);
        return Math.Min(MaximumChargingPercentage, Math.Clamp(estimate, previous, ceiling));
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

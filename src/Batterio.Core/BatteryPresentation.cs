namespace Batterio.Core;

/// <summary>
/// Framework-independent presentation values for a device row.
/// </summary>
public sealed record BatteryPresentation(
    BatteryChargeState State,
    int? Percentage,
    string PercentageText,
    bool IsPercentageVisible,
    int FilledSegments,
    bool IsCharging,
    bool IsOffline,
    string BatteryColorHex);

public static class BatteryPresentationRules
{
    public const string ChargingColorHex = "#22C55E";
    public const string LowColorHex = "#EF4444";
    public const string MediumColorHex = "#EAB308";
    public const string HighColorHex = "#22C55E";
    public const string OfflineColorHex = "#71717A";

    /// <summary>Fixed reserve-battery colour on the light popup, where white would disappear.</summary>
    public const string ReserveLightColorHex = "#18181B";

    /// <summary>Fixed reserve-battery colour on the dark popup.</summary>
    public const string ReserveDarkColorHex = "#FFFFFF";

    public static BatteryPresentation Create(BatteryReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        var percentage = reading.Percentage;
        var isCharging = reading.ChargeState == BatteryChargeState.Charging;
        var isOffline = reading.ChargeState == BatteryChargeState.Offline;
        return new BatteryPresentation(
            reading.ChargeState,
            percentage,
            percentage is int value ? $"{value}%" : string.Empty,
            percentage.HasValue,
            Math.Clamp(percentage.GetValueOrDefault() / 10, 0, 10),
            isCharging,
            isOffline,
            isCharging ? ChargingColorHex : GetPercentageColorHex(percentage));
    }

    /// <summary>
    /// Presentation of a reserve battery. It shares the segment, percentage and charging rules of the main battery,
    /// but its colour never changes with level or charge; <see cref="BatteryPresentation.BatteryColorHex"/> is the dark-theme value
    /// and the UI picks <see cref="ReserveLightColorHex"/> in the light theme. A null reserve gives an empty presentation without a percentage, and the UI hides the reserve icon entirely.
    /// </summary>
    public static BatteryPresentation CreateReserve(BatteryReading? reserve)
    {
        var presentation = Create(reserve ?? BatteryReading.Offline);
        return presentation with { BatteryColorHex = ReserveDarkColorHex };
    }

    public static string GetPercentageColorHex(int? percentage) => percentage switch
    {
        > 40 => HighColorHex,
        >= 20 => MediumColorHex,
        < 20 => LowColorHex,
        _ => OfflineColorHex
    };

    public static string GetStateDescription(BatteryChargeState state) => state switch
    {
        BatteryChargeState.Charging => "Charging",
        BatteryChargeState.NotCharging => "Not charging",
        BatteryChargeState.Offline => "Offline",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "The battery state is not supported.")
    };

    public static string GetAccessibilityDescription(string name, BatteryReading reading)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(reading);

        var description = $"{name}: {GetStateDescription(reading.ChargeState)}";
        return reading.Percentage is int percentage
            ? $"{description}, {percentage} percent"
            : description;
    }

    public static string GetAccessibilityDescription(string name, BatteryReading reading, bool hasReserveBattery)
    {
        var description = GetAccessibilityDescription(name, reading);
        if (!hasReserveBattery || reading.Reserve is not { } reserve)
        {
            return description;
        }

        return $"{description}. Reserve battery: {GetStateDescription(reserve.ChargeState)}" +
               (reserve.Percentage is int percentage ? $", {percentage} percent" : string.Empty);
    }
}

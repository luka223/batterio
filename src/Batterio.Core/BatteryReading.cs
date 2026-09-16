namespace Batterio.Core;

public enum BatteryChargeState
{
    Offline,
    NotCharging,
    Charging,
}

public sealed record BatteryReading
{
    public static BatteryReading Offline { get; } = new(null, BatteryChargeState.Offline);

    public BatteryReading(int? percentage, BatteryChargeState chargeState)
    {
        if (!Enum.IsDefined(chargeState))
        {
            throw new ArgumentOutOfRangeException(nameof(chargeState), chargeState, "Unsupported charge states are not valid enum values.");
        }

        if (percentage is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentage), percentage, "Battery percentage must be between 0 and 100.");
        }

        if (chargeState == BatteryChargeState.Offline && percentage is not null)
        {
            throw new ArgumentException("An offline battery reading cannot contain a percentage.", nameof(percentage));
        }

        Percentage = percentage;
        ChargeState = chargeState;
    }

    public int? Percentage { get; }

    public BatteryChargeState ChargeState { get; }

    public void Deconstruct(out int? percentage, out BatteryChargeState chargeState)
    {
        percentage = Percentage;
        chargeState = ChargeState;
    }
}

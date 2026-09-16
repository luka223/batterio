namespace Batterio.Core;

public sealed record DeviceBatteryState
{
    public required DeviceConfiguration Configuration { get; init; }

    public string? Error { get; init; }

    private BatteryReading _reading = BatteryReading.Offline;

    public BatteryReading Reading
    {
        get => _reading;
        init => _reading = value ?? throw new ArgumentNullException(nameof(value));
    }

    public BatteryReading CurrentReading
    {
        get => _reading;
        init => _reading = value ?? throw new ArgumentNullException(nameof(value));
    }

    public BatteryChargeState State => Reading.ChargeState;

    public int? Percentage => Reading.Percentage;
}

public sealed class DeviceBatteryStateChangedEventArgs(DeviceBatteryState state) : EventArgs
{
    public DeviceBatteryState State { get; } = state;
}

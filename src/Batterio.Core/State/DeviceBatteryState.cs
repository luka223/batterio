namespace Batterio.Core;

public enum BatteryRetrievalState
{
    Loading,
    Retrieving,
    Ready,
    Stale,
    Unavailable,
    Disabled,
    ConfigurationError
}

public sealed record DeviceBatteryState
{
    public required DeviceConfiguration Configuration { get; init; }

    public int? LastValidPercentage { get; init; }

    public BatteryRetrievalState RetrievalState { get; init; }

    public DateTimeOffset? LastSuccessfulUpdate { get; init; }

    public string? Error { get; init; }

    public string StatusText => RetrievalState switch
    {
        BatteryRetrievalState.Loading => "Loading…",
        BatteryRetrievalState.Retrieving => "Updating…",
        BatteryRetrievalState.Ready => $"{LastValidPercentage}%",
        BatteryRetrievalState.Stale when LastValidPercentage is int value => $"{value}% · Stale",
        BatteryRetrievalState.Stale => "Unavailable · Stale",
        BatteryRetrievalState.Unavailable => "Unavailable",
        BatteryRetrievalState.Disabled => "Disabled",
        BatteryRetrievalState.ConfigurationError => "Configuration error",
        _ => "Unavailable"
    };
}

public sealed class DeviceBatteryStateChangedEventArgs(DeviceBatteryState state) : EventArgs
{
    public DeviceBatteryState State { get; } = state;
}

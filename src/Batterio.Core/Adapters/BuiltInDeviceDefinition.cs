namespace Batterio.Core;

public sealed record BuiltInDeviceDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string AdapterId { get; init; }

    public required DeviceKind Kind { get; init; }

    /// <summary>The transports through which the device can be reached; its presence is the presence of any resolved route.</summary>
    public required IReadOnlyList<DeviceRouteDescriptor> Routes { get; init; }

    /// <summary>The device reports a second (reserve) battery that is shown beneath the primary one.</summary>
    public bool HasReserveBattery { get; init; }

    public DeviceConfiguration CreateConfiguration() => new()
    {
        Id = Id,
        Name = Name,
        AdapterId = AdapterId,
        Kind = Kind,
        Enabled = true,
        HasReserveBattery = HasReserveBattery
    };
}

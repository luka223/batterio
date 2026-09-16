namespace Batterio.Core;

public sealed record DeviceDiscoveryState
{
    public required BuiltInDeviceDefinition Device { get; init; }

    public bool IsPresent { get; init; }

    public string? Error { get; init; }

    public string DeviceId => Device.Id;

    public string Name => Device.Name;
}

public sealed record DeviceDiscoverySnapshot
{
    public DeviceDiscoverySnapshot(long revision, IReadOnlyList<DeviceDiscoveryState> devices)
    {
        Revision = revision;
        Devices = devices.ToArray();
    }

    public long Revision { get; }

    public IReadOnlyList<DeviceDiscoveryState> Devices { get; }

    public IReadOnlySet<string> PresentDeviceIds =>
        Devices.Where(device => device.IsPresent)
            .Select(device => device.DeviceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public sealed class DeviceDiscoverySnapshotChangedEventArgs(DeviceDiscoverySnapshot snapshot) : EventArgs
{
    public DeviceDiscoverySnapshot Snapshot { get; } = snapshot;
}

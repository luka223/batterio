namespace Batterio.Core;

public sealed record DeviceDiscoveryState
{
    public required BuiltInDeviceDefinition Device { get; init; }

    public bool IsPresent { get; init; }

    /// <summary>
    /// At least one route reports the device as connected right now. Only devices with routed transports
    /// (Bluetooth, Logi Bolt) can be online; devices detected by a HID collection alone never are.
    /// </summary>
    public bool IsOnline { get; init; }

    /// <summary>
    /// The device has at least one route and every one of them reports it gone (switched off, asleep, disconnected). A route whose state
    /// is unknown (a cable, a monitor that has not heard anything yet) keeps this false, so it is never inferred from <c>!IsOnline</c>.
    /// </summary>
    public bool IsReportedOffline { get; init; }

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

public sealed class DeviceCameOnlineEventArgs(string deviceId) : EventArgs
{
    public string DeviceId { get; } = deviceId;
}

public sealed class DeviceWentOfflineEventArgs(string deviceId) : EventArgs
{
    public string DeviceId { get; } = deviceId;
}

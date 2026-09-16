namespace Batterio.Core;

public sealed record DeviceSelectionState
{
    public required BuiltInDeviceDefinition Device { get; init; }

    public bool IsPresent { get; init; }

    public bool IsSelected { get; init; }

    public string? Error { get; init; }

    public string DeviceId => Device.Id;

    public string Name => Device.Name;
}

public sealed record DeviceSelectionSnapshot
{
    public DeviceSelectionSnapshot(
        long revision,
        bool isLoading,
        IReadOnlyList<DeviceSelectionState> devices,
        string? error = null)
    {
        Revision = revision;
        IsLoading = isLoading;
        Devices = devices.ToArray();
        Error = error;
    }

    public long Revision { get; }

    public bool IsLoading { get; }

    public IReadOnlyList<DeviceSelectionState> Devices { get; }

    public string? Error { get; }
}

public sealed class DeviceSelectionSnapshotChangedEventArgs(DeviceSelectionSnapshot snapshot) : EventArgs
{
    public DeviceSelectionSnapshot Snapshot { get; } = snapshot;
}

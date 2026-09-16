namespace Batterio.Core;

public sealed record PollingSnapshot
{
    public PollingSnapshot(long revision, IReadOnlyList<DeviceBatteryState> states)
    {
        Revision = revision;
        States = states.ToArray();
    }

    public long Revision { get; }

    public IReadOnlyList<DeviceBatteryState> States { get; }
}

public sealed class PollingSnapshotChangedEventArgs(PollingSnapshot snapshot) : EventArgs
{
    public PollingSnapshot Snapshot { get; } = snapshot;
}

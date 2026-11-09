using Batterio.Core;

namespace Batterio.Core.Abstractions;

public interface IDeviceDiscoveryService : IAsyncDisposable
{
    DeviceDiscoverySnapshot CurrentSnapshot { get; }

    event EventHandler<DeviceDiscoverySnapshotChangedEventArgs>? SnapshotChanged;

    /// <summary>
    /// A device whose transport reports it as connected was offline or of unknown state before (it woke up, or was switched back
    /// to this computer). Subscribers can read it immediately instead of waiting for the next poll.
    /// </summary>
    event EventHandler<DeviceCameOnlineEventArgs> DeviceCameOnline
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Every route of a device that has routes now reports it gone (switched off, asleep, disconnected), and the device allows its
    /// link signals to show it offline (<see cref="BuiltInDeviceDefinition.ShowsOfflineOnLinkLoss"/>). Raised once per transition, without
    /// any request to the device; subscribers decide from the current snapshot whether it is still gone.
    /// </summary>
    event EventHandler<DeviceWentOfflineEventArgs> DeviceWentOffline
    {
        add { }
        remove { }
    }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task<DeviceDiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken = default);

    Task<DeviceDiscoverySnapshot> RefreshAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}

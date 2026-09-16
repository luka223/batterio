using Batterio.Core;

namespace Batterio.Core.Abstractions;

public interface IDeviceDiscoveryService : IAsyncDisposable
{
    DeviceDiscoverySnapshot CurrentSnapshot { get; }

    event EventHandler<DeviceDiscoverySnapshotChangedEventArgs>? SnapshotChanged;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task<DeviceDiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken = default);

    Task<DeviceDiscoverySnapshot> RefreshAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}

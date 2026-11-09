using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Tells discovery whether the device behind a USB receiver route is reachable, using only what the receiver or the device
/// announces by itself (never a request sent to find out). Discovery calls <see cref="SyncAsync"/> after every enumeration, so a
/// source can open or close its listeners, and re-resolves the route states whenever <see cref="StateChanged"/> is raised.
/// </summary>
public interface IUsbRouteStateSource
{
    /// <summary>The state of the route over <paramref name="collection"/>, or null if this source knows nothing about it.</summary>
    RouteState? GetState(HidCollectionInfo collection);

    /// <summary>Some route state changed (a device woke up, went silent, or its link was lost).</summary>
    event EventHandler? StateChanged;

    /// <summary>Follows the HID collections discovery found; a source ignores those that are not its own.</summary>
    Task SyncAsync(IReadOnlyList<HidCollectionInfo> collections, CancellationToken cancellationToken = default);
}

using Batterio.Core.Abstractions;

namespace Batterio.Core;

public enum RouteState
{
    /// <summary>Nothing is known about whether the device is reachable; reading it is the only way to find out.</summary>
    Unknown,

    /// <summary>The transport reports the device as connected.</summary>
    Online,

    /// <summary>The transport reports the device as asleep, switched away or disconnected.</summary>
    Offline
}

/// <summary>
/// A transport through which a device is currently reachable, as resolved by discovery.
/// </summary>
/// <param name="DeviceId">The catalog device ID.</param>
/// <param name="Kind">The kind of transport.</param>
/// <param name="EndpointPath">The HID path of the collection that carries the traffic (for Bolt: the receiver's collection).</param>
/// <param name="Slot">The Logi Bolt pairing slot; null for other transports.</param>
/// <param name="State">What the transport reports about the device.</param>
/// <param name="Collection">The resolved HID collection, so a route can open it without enumerating again.</param>
public sealed record ResolvedRoute(
    string DeviceId,
    DeviceRouteKind Kind,
    string EndpointPath,
    byte? Slot,
    RouteState State,
    HidCollectionInfo? Collection = null)
{
    /// <summary>Identifies the physical route independently of its state.</summary>
    public string Key => Slot is byte slot ? $"{Kind}|{EndpointPath}|{slot}" : $"{Kind}|{EndpointPath}";
}

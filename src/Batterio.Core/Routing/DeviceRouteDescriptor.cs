using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>The transport through which a device can be reached.</summary>
public enum DeviceRouteKind
{
    /// <summary>The device is detected by the presence of a HID collection; its adapter finds the endpoint itself.</summary>
    HidPresence,

    /// <summary>The device is connected directly over Bluetooth LE and exposes its own HID++ collection.</summary>
    HidppBluetooth,

    /// <summary>The device is paired to a Logi Bolt receiver and is addressed by its slot.</summary>
    LogiBolt
}

/// <summary>
/// Declares, as catalog data, one transport through which a built-in device can be reached.
/// Descriptors are static; discovery turns them into <see cref="ResolvedRoute"/>s at runtime.
/// </summary>
public abstract record DeviceRouteDescriptor
{
    public abstract DeviceRouteKind Kind { get; }
}

/// <summary>A device that is present as soon as a matching HID collection exists (every device that has no routed adapter).</summary>
public sealed record HidPresenceRoute(HidCollectionQuery Query) : DeviceRouteDescriptor
{
    public override DeviceRouteKind Kind => DeviceRouteKind.HidPresence;
}

/// <summary>A device with its own Bluetooth LE HID++ collection, identified by the product ID in the HID path.</summary>
public sealed record HidppBluetoothRouteDescriptor(ushort ProductId) : DeviceRouteDescriptor
{
    public override DeviceRouteKind Kind => DeviceRouteKind.HidppBluetooth;
}

/// <summary>
/// A device paired to a Logi Bolt receiver, identified by its wireless product ID.
/// <paramref name="Names"/> are alternative device names used to recognise a slot when only a scan (no connection notices) is available.
/// </summary>
public sealed record LogiBoltRouteDescriptor(ushort WirelessProductId, IReadOnlySet<string>? Names = null) : DeviceRouteDescriptor
{
    public override DeviceRouteKind Kind => DeviceRouteKind.LogiBolt;
}

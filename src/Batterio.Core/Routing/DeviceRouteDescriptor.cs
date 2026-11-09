using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>The transport through which a device can be reached.</summary>
public enum DeviceRouteKind
{
    /// <summary>The device itself is connected over USB (for example a mouse that is plugged in to charge) and exposes its own HID collection.</summary>
    UsbCable,

    /// <summary>The device talks to a USB dongle or base station dedicated to it, which exposes the HID collection.</summary>
    UsbReceiver,

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

/// <summary>
/// A device reached through a USB HID collection: every collection that matches <paramref name="Query"/> is one route of kind
/// <paramref name="Kind"/>, which must be <see cref="DeviceRouteKind.UsbCable"/> or <see cref="DeviceRouteKind.UsbReceiver"/>.
/// </summary>
public sealed record UsbHidRouteDescriptor : DeviceRouteDescriptor
{
    public UsbHidRouteDescriptor(DeviceRouteKind kind, HidCollectionQuery query)
    {
        if (kind is not (DeviceRouteKind.UsbCable or DeviceRouteKind.UsbReceiver))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A USB HID route is either a cable or a receiver route.");
        }

        ArgumentNullException.ThrowIfNull(query);
        Kind = kind;
        Query = query;
    }

    public override DeviceRouteKind Kind { get; }

    public HidCollectionQuery Query { get; }
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

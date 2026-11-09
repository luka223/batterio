using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Logitech G Pro X2 Superstrike: reached through its USB cable (product C0A8, preferred while it is plugged in to charge) or through
/// its Lightspeed receiver (product C54D), both on HID++ interface 2.
/// </summary>
public static class LogitechGProX2Superstrike
{
    public const string AdapterId = "logitech-g-pro-x2-superstrike";
    public const ushort VendorId = 0x046D;
    public const ushort ReceiverProductId = 0xC54D;
    public const ushort WiredProductId = 0xC0A8;
    public const ushort InterfaceNumber = 2;
    public const ushort ExpectedWirelessModelId = 0x40BD;
    public const ushort ExpectedUsbModelId = 0xC0A8;

    /// <summary>The mouse answers on this HID++ device index when it is addressed directly (over its cable).</summary>
    public const byte CableDeviceIndex = 0xFF;

    public const byte EQuadTransportFlag = 0x04;
    public const byte UsbTransportFlag = 0x08;

    public static readonly HidCollectionQuery ReceiverQuery = new()
    {
        VendorId = VendorId,
        ProductId = ReceiverProductId,
        InterfaceNumber = InterfaceNumber,
        UsagePage = 0xFF00,
        MinimumReportLength = 20,
        RequiredReportId = 0x11
    };

    public static readonly HidCollectionQuery WiredQuery = new()
    {
        VendorId = VendorId,
        ProductId = WiredProductId,
        InterfaceNumber = InterfaceNumber,
        UsagePage = 0xFF00,
        Usage = 0x0002,
        MinimumReportLength = 20,
        RequiredReportId = 0x11
    };

    /// <summary>The cable comes first: while it is plugged in the mouse is awake and charging, whatever the receiver reports.</summary>
    public static IReadOnlyList<DeviceRouteDescriptor> Routes { get; } =
    [
        new UsbHidRouteDescriptor(DeviceRouteKind.UsbCable, WiredQuery),
        new UsbHidRouteDescriptor(DeviceRouteKind.UsbReceiver, ReceiverQuery)
    ];

    public static RoutedDeviceProfile Profile { get; } = new(
        AdapterId,
        "Logitech G Pro X2 Superstrike",
        "No supported Logitech Superstrike wired USB or receiver HID++ endpoint was found.",
        Routes,
        context => new Dictionary<DeviceRouteKind, IBatteryRoute>
        {
            [DeviceRouteKind.UsbCable] = new SuperstrikeCableRoute(context.Provider, context.Log),
            [DeviceRouteKind.UsbReceiver] = new SuperstrikeReceiverRoute(context.Provider, context.Log)
        });
}

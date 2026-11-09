using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>Logitech G Pro X2 Lightspeed headset: reached through its USB receiver (product 0AF7, Centurion protocol).</summary>
/// <remarks>
/// Hardware findings on wake-ups: none recorded yet. The receiver is reacted to when it is plugged in or removed; a headset waking
/// up is noticed at the next poll, when the popup opens, or on Refresh (see the project's routes notes).
/// </remarks>
public static class LogitechGProX2Lightspeed
{
    public const string AdapterId = "logitech-g-pro-x2-lightspeed";
    public const ushort VendorId = 0x046D;
    public const ushort ProductId = 0x0AF7;
    public const ushort InterfaceNumber = 3;
    public const ushort UsagePage = 0xFFA0;
    public const ushort Usage = 0x0001;

    public static readonly HidCollectionQuery LightspeedQuery = new()
    {
        VendorId = VendorId,
        ProductId = ProductId,
        InterfaceNumber = InterfaceNumber,
        UsagePage = UsagePage,
        Usage = Usage,
        MinimumReportLength = 64,
        RequiredReportId = 0x51
    };

    public static IReadOnlyList<DeviceRouteDescriptor> Routes { get; } =
    [
        new UsbHidRouteDescriptor(DeviceRouteKind.UsbReceiver, LightspeedQuery)
    ];

    public static RoutedDeviceProfile Profile { get; } = new(
        AdapterId,
        "Logitech G Pro X2 Lightspeed",
        "The Logitech LIGHTSPEED headset receiver was not found.",
        Routes,
        context => new Dictionary<DeviceRouteKind, IBatteryRoute>
        {
            [DeviceRouteKind.UsbReceiver] = new LightspeedCenturionRoute(context.Provider, context.Log)
        });
}

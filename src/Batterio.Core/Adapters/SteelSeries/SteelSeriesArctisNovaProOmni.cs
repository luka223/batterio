using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// SteelSeries Arctis Nova Pro Omni: reached through its base station (hub) in USB-1 mode. It has a reserve battery that the hub reports
/// together with the one in the headset (<see cref="BuiltInDeviceDefinition.HasReserveBattery"/>).
/// </summary>
/// <remarks>
/// Hardware findings on wake-ups (2026-11-09, listen-only captures while the headset was switched off and on, hub staying connected):
/// the status collection (interface 3, usage page 0xFFC0) sends no frame at all, but the hub's other collection on that interface (usage
/// page 0xFF00) announces a headset connecting or disconnecting with a <c>07 B5</c> event, which <see cref="SteelSeriesHubMonitor"/>
/// listens to. See there for the frames. The event follows the button press by about 5 s (boot plus connecting to GameHub).
/// </remarks>
public static class SteelSeriesArctisNovaProOmni
{
    public const string AdapterId = "steelseries-arctis-nova-pro-omni";

    public const ushort VendorId = 0x1038;

    /// <summary>Arctis Nova Pro Omni hub in USB-1 mode. USB-2 (0x2292) and XBOX (0x2293) modes stay silent.</summary>
    public const ushort NovaProOmniProductId = 0x2290;

    /// <summary>Vendor status collection (interface 3, report ID 1, 64-byte reports) measured on a real hub.</summary>
    public static readonly HidCollectionQuery NovaProOmniQuery = new()
    {
        VendorId = VendorId,
        ProductId = NovaProOmniProductId,
        InterfaceNumber = 3,
        UsagePage = 0xFFC0,
        Usage = 0x0001,
        RequiredReportId = 0x01,
        MinimumReportLength = 64
    };

    /// <summary>
    /// The hub's second collection on interface 3 (usage page 0xFF00, 64-byte reports), where it announces the headset connecting and
    /// disconnecting. It is only listened to, never a route.
    /// </summary>
    public static readonly HidCollectionQuery EventsQuery = new()
    {
        VendorId = VendorId,
        ProductId = NovaProOmniProductId,
        InterfaceNumber = 3,
        UsagePage = 0xFF00,
        Usage = 0x0001
    };

    public static IReadOnlyList<DeviceRouteDescriptor> Routes { get; } =
    [
        new UsbHidRouteDescriptor(DeviceRouteKind.UsbReceiver, NovaProOmniQuery)
    ];

    public static RoutedDeviceProfile Profile { get; } = new(
        AdapterId,
        "SteelSeries Arctis Nova Pro Omni",
        "The SteelSeries Arctis Nova Pro Omni hub was not found (set the hub switch to USB-1).",
        Routes,
        context => new Dictionary<DeviceRouteKind, IBatteryRoute>
        {
            [DeviceRouteKind.UsbReceiver] = new SteelSeriesHubRoute(
                context.Provider, context.Log, context.Timings.ReplyTimeout, context.Timings.RetryDelay)
        });
}

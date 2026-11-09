using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Keychron Q1 HE: its battery is read from reports the user's custom firmware pushes through the Keychron Link dongle
/// (the keyboard ignores requests over 2.4 GHz, so Batterio only ever listens). Hardware-verified; no other way to read it is supported.
/// </summary>
public static class KeychronQ1He
{
    public const string AdapterId = "keychron-q1-he";

    public const ushort VendorId = 0x3434;

    /// <summary>Keychron Link 2.4 GHz dongle.</summary>
    public const ushort LinkProductId = 0xD030;

    /// <summary>First payload byte of the battery report pushed by the custom firmware.</summary>
    public const byte BatteryReportCommand = 0xA4;

    /// <summary>Model id reported by the custom firmware for the Q1 HE (the K10 HE uses 1).</summary>
    public const byte Q1HeModelId = 0x02;

    /// <summary>
    /// Raw HID collection of the Link dongle (interface 3, usage page 0xFF60, usage 0x61, 33-byte reports including
    /// report ID 0), measured on a real dongle. No required report ID: the collection uses report ID 0.
    /// </summary>
    public static readonly HidCollectionQuery LinkRawHidQuery = new()
    {
        VendorId = VendorId,
        ProductId = LinkProductId,
        InterfaceNumber = 3,
        UsagePage = 0xFF60,
        Usage = 0x0061,
        MinimumReportLength = 33
    };

    public static IReadOnlyList<DeviceRouteDescriptor> Routes { get; } =
    [
        new UsbHidRouteDescriptor(DeviceRouteKind.UsbReceiver, LinkRawHidQuery)
    ];

    public static RoutedDeviceProfile Profile { get; } = new(
        AdapterId,
        "Keychron Q1 HE",
        "The Keychron Link receiver was not found.",
        Routes,
        context => new Dictionary<DeviceRouteKind, IBatteryRoute>
        {
            [DeviceRouteKind.UsbReceiver] = new KeychronLinkRoute(
                context.Provider, context.Log, context.Timings.ListenTimeout, context.TimeProvider, context.KeychronLinks)
        });
}

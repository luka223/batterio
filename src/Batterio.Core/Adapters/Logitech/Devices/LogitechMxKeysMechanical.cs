namespace Batterio.Core;

/// <summary>
/// Logitech MX Keys Mechanical: reachable over Bluetooth LE (verified on hardware) or through a Logi Bolt receiver (experimental).
/// Product ID 0xB366, Device Name "MX Mechanical".
/// </summary>
public static class LogitechMxKeysMechanical
{
    public const string AdapterId = "logitech-mx-keys-mechanical";
    public const ushort ProductId = 0xB366;

    /// <summary>What the device reports about itself, used to recognise it in a Logi Bolt pairing table.</summary>
    public static LogitechHidppDeviceProfile Identity { get; } = new(
        AdapterId,
        "Logitech MX Keys Mechanical",
        new HashSet<ushort> { ProductId },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MX Mechanical", "MX Keys Mechanical" });

    public static IReadOnlyList<DeviceRouteDescriptor> Routes { get; } =
    [
        new HidppBluetoothRouteDescriptor(ProductId),
        new LogiBoltRouteDescriptor(ProductId, Identity.ExpectedNames)
    ];

    public static RoutedDeviceProfile Profile { get; } = new(
        AdapterId,
        Identity.DisplayName,
        $"No Bluetooth connection to {Identity.DisplayName} and no Logi Bolt receiver with it paired was found.",
        Routes,
        context => LogitechHidppRoutes.Create(Identity.DisplayName, context));
}

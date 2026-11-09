namespace Batterio.Core;

/// <summary>
/// Logitech MX Master 3S: reachable over Bluetooth LE (verified on hardware) or through a Logi Bolt receiver (experimental).
/// Product ID 0xB034, Device Name "MX Master 3S".
/// </summary>
public static class LogitechMxMaster3s
{
    public const string AdapterId = "logitech-mx-master-3s";
    public const ushort ProductId = 0xB034;

    /// <summary>What the device reports about itself, used to recognise it in a Logi Bolt pairing table.</summary>
    public static LogitechHidppDeviceProfile Identity { get; } = new(
        AdapterId,
        "Logitech MX Master 3S",
        new HashSet<ushort> { ProductId },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MX Master 3S" });

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

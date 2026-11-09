namespace Batterio.Core;

/// <summary>Devices shipped with the app. Add a new device here (catalog and profile) and the app registers its adapter factory.</summary>
public static class BuiltInDevices
{
    public static IReadOnlyList<BuiltInDeviceDefinition> Catalog { get; } =
    [
        new BuiltInDeviceDefinition
        {
            Id = KeychronQ1He.AdapterId,
            Name = "Keychron Q1 HE",
            AdapterId = KeychronQ1He.AdapterId,
            Kind = DeviceKind.Keyboard,
            Routes = KeychronQ1He.Profile.Routes
        },
        new BuiltInDeviceDefinition
        {
            Id = LogitechGProX2Superstrike.AdapterId,
            Name = "Logitech G Pro X2 Superstrike",
            AdapterId = LogitechGProX2Superstrike.AdapterId,
            Kind = DeviceKind.Mouse,
            Routes = LogitechGProX2Superstrike.Profile.Routes
        },
        new BuiltInDeviceDefinition
        {
            Id = LogitechGProX2Lightspeed.AdapterId,
            Name = "Logitech G Pro X2 Lightspeed",
            AdapterId = LogitechGProX2Lightspeed.AdapterId,
            Kind = DeviceKind.Mouse,
            Routes = LogitechGProX2Lightspeed.Profile.Routes
        },
        new BuiltInDeviceDefinition
        {
            Id = SteelSeriesArctisNovaProOmni.AdapterId,
            Name = "SteelSeries Arctis Nova Pro Omni",
            AdapterId = SteelSeriesArctisNovaProOmni.AdapterId,
            Kind = DeviceKind.Headset,
            HasReserveBattery = true,
            Routes = SteelSeriesArctisNovaProOmni.Profile.Routes
        },
        new BuiltInDeviceDefinition
        {
            Id = LogitechMxMaster3s.AdapterId,
            Name = "Logitech MX Master 3S",
            AdapterId = LogitechMxMaster3s.AdapterId,
            Kind = DeviceKind.Mouse,
            Routes = LogitechMxMaster3s.Profile.Routes,
            // Bluetooth and Bolt "disconnected" signals are not hardware-verified yet; the regular poll shows it Offline.
            ShowsOfflineOnLinkLoss = false
        },
        new BuiltInDeviceDefinition
        {
            Id = LogitechMxKeysMechanical.AdapterId,
            Name = "Logitech MX Keys Mechanical",
            AdapterId = LogitechMxKeysMechanical.AdapterId,
            Kind = DeviceKind.Keyboard,
            Routes = LogitechMxKeysMechanical.Profile.Routes,
            // Bluetooth and Bolt "disconnected" signals are not hardware-verified yet; the regular poll shows it Offline.
            ShowsOfflineOnLinkLoss = false
        }
    ];

    public static IReadOnlyDictionary<string, BuiltInDeviceDefinition> CatalogById { get; } =
        Catalog.ToDictionary(device => device.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>How each built-in device is read, keyed by adapter ID: its identity, transports and route implementations.</summary>
    public static IReadOnlyList<RoutedDeviceProfile> Profiles { get; } =
    [
        KeychronQ1He.Profile,
        LogitechGProX2Superstrike.Profile,
        LogitechGProX2Lightspeed.Profile,
        SteelSeriesArctisNovaProOmni.Profile,
        LogitechMxMaster3s.Profile,
        LogitechMxKeysMechanical.Profile
    ];

    public static IReadOnlyList<DeviceConfiguration> Create() =>
        Catalog.Select(device => device.CreateConfiguration()).ToArray();
}

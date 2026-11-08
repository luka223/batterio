namespace Batterio.Core;

/// <summary>Devices shipped with the app. Add new devices here and register their adapter factories.</summary>
public static class BuiltInDevices
{
    public static IReadOnlyList<BuiltInDeviceDefinition> Catalog { get; } =
    [
        new BuiltInDeviceDefinition
        {
            Id = "keychron-q1-he",
            Name = "Keychron Q1 HE",
            AdapterId = "keychron-q1-he",
            Kind = DeviceKind.Keyboard,
            Routes = [new HidPresenceRoute(KeychronDefinitions.LinkRawHidQuery)]
        },
        new BuiltInDeviceDefinition
        {
            Id = "logitech-g-pro-x2-superstrike",
            Name = "Logitech G Pro X2 Superstrike",
            AdapterId = "logitech-g-pro-x2-superstrike",
            Kind = DeviceKind.Mouse,
            Routes =
            [
                new HidPresenceRoute(LogitechReceiverDefinitions.SuperstrikeWiredQuery),
                new HidPresenceRoute(LogitechReceiverDefinitions.SuperstrikeReceiverQuery)
            ]
        },
        new BuiltInDeviceDefinition
        {
            Id = "logitech-g-pro-x2-lightspeed",
            Name = "Logitech G Pro X2 Lightspeed",
            AdapterId = "logitech-g-pro-x2-lightspeed",
            Kind = DeviceKind.Mouse,
            Routes = [new HidPresenceRoute(LogitechReceiverDefinitions.LightspeedQuery)]
        },
        new BuiltInDeviceDefinition
        {
            Id = "steelseries-arctis-nova-pro-omni",
            Name = "SteelSeries Arctis Nova Pro Omni",
            AdapterId = "steelseries-arctis-nova-pro-omni",
            Kind = DeviceKind.Headset,
            HasReserveBattery = true,
            Routes = [new HidPresenceRoute(SteelSeriesDefinitions.NovaProOmniQuery)]
        },
        new BuiltInDeviceDefinition
        {
            Id = LogitechMxMaster3s.AdapterId,
            Name = "Logitech MX Master 3S",
            AdapterId = LogitechMxMaster3s.AdapterId,
            Kind = DeviceKind.Mouse,
            Routes = LogitechMxMaster3s.Routes
        },
        new BuiltInDeviceDefinition
        {
            Id = LogitechMxKeysMechanical.AdapterId,
            Name = "Logitech MX Keys Mechanical",
            AdapterId = LogitechMxKeysMechanical.AdapterId,
            Kind = DeviceKind.Keyboard,
            Routes = LogitechMxKeysMechanical.Routes
        }
    ];

    public static IReadOnlyDictionary<string, BuiltInDeviceDefinition> CatalogById { get; } =
        Catalog.ToDictionary(device => device.Id, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<DeviceConfiguration> Create() =>
        Catalog.Select(device => device.CreateConfiguration()).ToArray();
}

namespace Batterio.Core;

/// <summary>Devices shipped with the app. Add new devices here and register their adapter factories.</summary>
public static class BuiltInDevices
{
    public static IReadOnlyList<BuiltInDeviceDefinition> Catalog { get; } =
    [
        new BuiltInDeviceDefinition
        {
            Id = "logitech-g-pro-x2-superstrike",
            Name = "Logitech G Pro X2 Superstrike",
            AdapterId = "logitech-g-pro-x2-superstrike",
            PresenceQueries =
            [
                LogitechReceiverDefinitions.SuperstrikeWiredQuery,
                LogitechReceiverDefinitions.SuperstrikeReceiverQuery
            ]
        },
        new BuiltInDeviceDefinition
        {
            Id = "logitech-g-pro-x2-lightspeed",
            Name = "Logitech G Pro X2 Lightspeed",
            AdapterId = "logitech-g-pro-x2-lightspeed",
            PresenceQueries = [LogitechReceiverDefinitions.LightspeedQuery]
        },
        new BuiltInDeviceDefinition
        {
            Id = "steelseries-arctis-nova-pro-omni",
            Name = "SteelSeries Arctis Nova Pro Omni",
            AdapterId = "steelseries-arctis-nova-pro-omni",
            HasReserveBattery = true,
            PresenceQueries = [SteelSeriesDefinitions.NovaProOmniQuery]
        },
        new BuiltInDeviceDefinition
        {
            Id = "logitech-mx-master-3s",
            Name = "Logitech MX Master 3S",
            AdapterId = "logitech-mx-master-3s",
            PresenceQueries = [LogitechReceiverDefinitions.BoltReceiverQuery]
        },
        new BuiltInDeviceDefinition
        {
            Id = "logitech-mx-keys-mechanical",
            Name = "Logitech MX Keys Mechanical",
            AdapterId = "logitech-mx-keys-mechanical",
            PresenceQueries = [LogitechReceiverDefinitions.BoltReceiverQuery]
        }
    ];

    public static IReadOnlyDictionary<string, BuiltInDeviceDefinition> CatalogById { get; } =
        Catalog.ToDictionary(device => device.Id, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<DeviceConfiguration> Create() =>
        Catalog.Select(device => device.CreateConfiguration()).ToArray();
}

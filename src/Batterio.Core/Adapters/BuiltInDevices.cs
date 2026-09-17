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
        }
    ];

    public static IReadOnlyDictionary<string, BuiltInDeviceDefinition> CatalogById { get; } =
        Catalog.ToDictionary(device => device.Id, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<DeviceConfiguration> Create() =>
        Catalog.Select(device => device.CreateConfiguration()).ToArray();
}

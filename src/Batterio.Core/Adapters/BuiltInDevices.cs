namespace Batterio.Core;

/// <summary>Devices shipped with the app. Add new devices here and register their adapter factories.</summary>
public static class BuiltInDevices
{
    public static IReadOnlyList<DeviceConfiguration> Create() =>
    [
        new DeviceConfiguration
        {
            Id = "logitech-g-pro-x2-superstrike",
            Name = "Logitech G Pro X2 Superstrike",
            AdapterId = "logitech-g-pro-x2-superstrike"
        },
        new DeviceConfiguration
        {
            Id = "keychron-q1-he",
            Name = "Keychron Q1 HE",
            AdapterId = "keychron-q1-he"
        },
        new DeviceConfiguration
        {
            Id = "logitech-g-pro-x2-lightspeed",
            Name = "Logitech G Pro X2 Lightspeed",
            AdapterId = "logitech-g-pro-x2-lightspeed"
        }
    ];
}

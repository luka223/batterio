namespace Batterio.Core;

/// <summary>Builds the route implementations that Logitech devices reachable over Bluetooth LE and Logi Bolt share.</summary>
internal static class LogitechHidppRoutes
{
    public static IReadOnlyDictionary<DeviceRouteKind, IBatteryRoute> Create(string displayName, RouteContext context) =>
        new Dictionary<DeviceRouteKind, IBatteryRoute>
        {
            [DeviceRouteKind.HidppBluetooth] = new HidppBluetoothRoute(displayName, context.Provider, context.Log),
            [DeviceRouteKind.LogiBolt] = new LogiBoltSlotRoute(displayName, context.Receivers, context.Log)
        };
}

using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>Builds the route implementations that Logitech devices reachable over Bluetooth LE and Logi Bolt share.</summary>
public static class LogitechRoutedAdapters
{
    /// <summary>
    /// Composes a <see cref="RoutedBatteryAdapter"/> for <paramref name="profile"/> from the Bluetooth and Logi Bolt routes.
    /// </summary>
    public static IBatteryAdapter Create(
        LogitechHidppDeviceProfile profile,
        IHidCollectionProvider provider,
        LogiBoltReceiverManager receivers,
        IDeviceRouteTable routes,
        IBatterioLog log,
        string? devicePath)
    {
        var implementations = new Dictionary<DeviceRouteKind, IBatteryRoute>
        {
            [DeviceRouteKind.HidppBluetooth] = new HidppBluetoothRoute(profile.DisplayName, provider, log),
            [DeviceRouteKind.LogiBolt] = new LogiBoltSlotRoute(profile.DisplayName, receivers, log)
        };
        return new RoutedBatteryAdapter(profile.AdapterId, profile.DisplayName, implementations, routes, log, devicePath);
    }
}

using System.Text.Json;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Logitech MX Keys Mechanical: reachable over Bluetooth LE (verified on hardware) or through a Logi Bolt receiver (experimental).
/// Product ID 0xB366, Device Name "MX Mechanical".
/// </summary>
public static class LogitechMxKeysMechanical
{
    public const string AdapterId = "logitech-mx-keys-mechanical";
    public const ushort ProductId = 0xB366;

    public static LogitechHidppDeviceProfile Profile { get; } = new(
        AdapterId,
        "Logitech MX Keys Mechanical",
        new HashSet<ushort> { ProductId },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MX Mechanical", "MX Keys Mechanical" });

    public static IReadOnlyList<DeviceRouteDescriptor> Routes { get; } =
    [
        new HidppBluetoothRouteDescriptor(ProductId),
        new LogiBoltRouteDescriptor(ProductId, Profile.ExpectedNames)
    ];
}

public sealed class LogitechMxKeysMechanicalAdapterFactory : IBatteryAdapterFactory
{
    private readonly IHidCollectionProvider _provider;
    private readonly LogiBoltReceiverManager _receivers;
    private readonly IDeviceRouteTable _routes;
    private readonly IBatterioLog _log;

    /// <summary>A factory without discovery: no route is ever resolved, so its adapters report the receiver as missing.</summary>
    public LogitechMxKeysMechanicalAdapterFactory()
        : this(new EmptyHidCollectionProvider(), null)
    {
    }

    /// <summary>A factory with its own receivers and route table, for use without the app's shared discovery.</summary>
    public LogitechMxKeysMechanicalAdapterFactory(IHidCollectionProvider provider, IBatterioLog? log = null)
        : this(provider, new LogiBoltReceiverManager(provider, log), new DeviceRouteTable(), log)
    {
    }

    public LogitechMxKeysMechanicalAdapterFactory(
        IHidCollectionProvider provider,
        LogiBoltReceiverManager receivers,
        IDeviceRouteTable routes,
        IBatterioLog? log = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _receivers = receivers ?? throw new ArgumentNullException(nameof(receivers));
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _log = log ?? new NullBatterioLog();
    }

    public string AdapterId => LogitechMxKeysMechanical.AdapterId;

    public void ValidateSettings(JsonElement settings) => HidAdapterSettings.ReadDevicePath(settings, AdapterId);

    public IBatteryAdapter CreateAdapter(DeviceConfiguration device) =>
        LogitechRoutedAdapters.Create(
            LogitechMxKeysMechanical.Profile, _provider, _receivers, _routes, _log,
            HidAdapterSettings.ReadDevicePath(device.AdapterSettings, AdapterId));
}

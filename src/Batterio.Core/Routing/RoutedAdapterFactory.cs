using System.Text.Json;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// The one adapter factory for every device: it validates the <c>devicePath</c> setting and composes a
/// <see cref="RoutedBatteryAdapter"/> from the routes of a <see cref="RoutedDeviceProfile"/>.
/// </summary>
public sealed class RoutedAdapterFactory : IBatteryAdapterFactory
{
    private readonly RoutedDeviceProfile _profile;
    private readonly RouteContext _context;
    private readonly IDeviceRouteTable _routes;

    /// <summary>A factory without discovery: no route is ever resolved, so its adapters report the device as missing.</summary>
    public RoutedAdapterFactory(RoutedDeviceProfile profile)
        : this(profile, new EmptyHidCollectionProvider(), null)
    {
    }

    /// <summary>A factory with its own receivers and route table, for use without the app's shared discovery.</summary>
    public RoutedAdapterFactory(RoutedDeviceProfile profile, IHidCollectionProvider provider, IBatterioLog? log = null)
        : this(profile, provider, new LogiBoltReceiverManager(provider, log), new DeviceRouteTable(), log)
    {
    }

    public RoutedAdapterFactory(
        RoutedDeviceProfile profile,
        IHidCollectionProvider provider,
        LogiBoltReceiverManager receivers,
        IDeviceRouteTable routes,
        IBatterioLog? log = null,
        TimeProvider? timeProvider = null,
        RouteTimings? timings = null,
        KeychronLinkMonitor? keychronLinks = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(receivers);
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _context = new RouteContext(
            provider, receivers, log ?? new NullBatterioLog(), timeProvider ?? TimeProvider.System, timings ?? new RouteTimings(), keychronLinks);
    }

    public string AdapterId => _profile.AdapterId;

    public RoutedDeviceProfile Profile => _profile;

    public void ValidateSettings(JsonElement settings) => HidAdapterSettings.ReadDevicePath(settings, AdapterId);

    public IBatteryAdapter CreateAdapter(DeviceConfiguration device) =>
        new RoutedBatteryAdapter(
            _profile.AdapterId,
            _profile.DisplayName,
            _profile.CreateRoutes(_context),
            _routes,
            _context.Log,
            HidAdapterSettings.ReadDevicePath(device.AdapterSettings, AdapterId),
            _profile.MissingMessage);
}

using System.Text.Json;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

/// <summary>
/// Builds the adapter a device really gets, without a running discovery: it resolves the device's routes once from what a fake
/// provider enumerates (through <see cref="DeviceRouteResolver"/>, as discovery does), fills a <see cref="DeviceRouteTable"/> and asks
/// the device's <see cref="RoutedAdapterFactory"/> for the adapter. <see cref="Resolve"/> repeats the resolution after the fake changed.
/// </summary>
internal sealed class RouteHarness
{
    private readonly BuiltInDeviceDefinition _definition;
    private readonly IHidCollectionProvider _provider;
    private readonly DeviceRouteResolver _resolver;

    public RouteHarness(
        RoutedDeviceProfile profile,
        IHidCollectionProvider provider,
        IBatterioLog? log = null,
        TimeProvider? timeProvider = null,
        RouteTimings? timings = null,
        IEnumerable<IUsbRouteStateSource>? usbSources = null,
        KeychronLinkMonitor? keychronLinks = null)
    {
        _definition = BuiltInDevices.CatalogById[profile.AdapterId];
        _provider = provider;
        _resolver = new DeviceRouteResolver(usbSources: usbSources);
        Table = new DeviceRouteTable();
        Factory = new RoutedAdapterFactory(
            profile, provider, new LogiBoltReceiverManager(provider, log), Table, log, timeProvider, timings, keychronLinks);
        Resolve();
    }

    public DeviceRouteTable Table { get; }

    public RoutedAdapterFactory Factory { get; }

    public IReadOnlyList<ResolvedRoute> Routes => Table.GetRoutes(_definition.Id);

    /// <summary>Resolves the routes again from what the provider enumerates now, and publishes them.</summary>
    public IReadOnlyList<ResolvedRoute> Resolve()
    {
        var queries = _definition.Routes.SelectMany(DeviceRouteResolver.QueriesOf).Distinct().ToArray();
        var collections = _provider.Enumerate(queries) ?? Array.Empty<HidCollectionInfo>();
        var (routes, _) = _resolver.Resolve(_definition, collections.ToArray(), new Dictionary<HidCollectionQuery, Exception>());
        Table.SetRoutes(_definition.Id, routes);
        return routes;
    }

    public IBatteryAdapter CreateAdapter(string? devicePath = null)
    {
        using var settings = JsonDocument.Parse(devicePath is null
            ? "{}"
            : JsonSerializer.Serialize(new Dictionary<string, string> { ["devicePath"] = devicePath }));
        return Factory.CreateAdapter(new DeviceConfiguration
        {
            Id = _definition.Id,
            Name = _definition.Name,
            AdapterId = _definition.AdapterId,
            AdapterSettings = settings.RootElement.Clone()
        });
    }

    public static IBatteryAdapter Create(
        RoutedDeviceProfile profile,
        IHidCollectionProvider provider,
        string? devicePath = null,
        IBatterioLog? log = null,
        TimeProvider? timeProvider = null,
        RouteTimings? timings = null) =>
        new RouteHarness(profile, provider, log, timeProvider, timings).CreateAdapter(devicePath);

    public static IBatteryAdapter Superstrike(IHidCollectionProvider provider, string? devicePath = null, IBatterioLog? log = null) =>
        Create(LogitechGProX2Superstrike.Profile, provider, devicePath, log);

    public static IBatteryAdapter Lightspeed(IHidCollectionProvider provider, string? devicePath = null, IBatterioLog? log = null) =>
        Create(LogitechGProX2Lightspeed.Profile, provider, devicePath, log);

    public static IBatteryAdapter Keychron(
        IHidCollectionProvider provider,
        string? devicePath = null,
        IBatterioLog? log = null,
        TimeProvider? timeProvider = null,
        TimeSpan? listenTimeout = null) =>
        Create(KeychronQ1He.Profile, provider, devicePath, log, timeProvider, new RouteTimings { ListenTimeout = listenTimeout });

    public static IBatteryAdapter Omni(
        IHidCollectionProvider provider,
        string? devicePath = null,
        IBatterioLog? log = null,
        TimeSpan? replyTimeout = null,
        TimeSpan? retryDelay = null) =>
        Create(SteelSeriesArctisNovaProOmni.Profile, provider, devicePath, log, null,
            new RouteTimings { ReplyTimeout = replyTimeout, RetryDelay = retryDelay });
}

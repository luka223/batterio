using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>Timeouts a route may be given instead of its defaults. Only tests set them.</summary>
public sealed record RouteTimings
{
    /// <summary>How long a listen-only route waits for the device to push its report.</summary>
    public TimeSpan? ListenTimeout { get; init; }

    /// <summary>How long a request-reply route waits for the answer.</summary>
    public TimeSpan? ReplyTimeout { get; init; }

    /// <summary>The settle delay before a route retries a device that was busy.</summary>
    public TimeSpan? RetryDelay { get; init; }
}

/// <summary>What a device's route implementations are built from. One context is shared by every adapter of a factory.</summary>
public sealed record RouteContext(
    IHidCollectionProvider Provider,
    LogiBoltReceiverManager Receivers,
    IBatterioLog Log,
    TimeProvider TimeProvider,
    RouteTimings Timings,
    KeychronLinkMonitor? KeychronLinks = null);

/// <summary>
/// Everything that makes a catalog device a <see cref="RoutedBatteryAdapter"/>: its identity, the transports it can be reached through
/// (the same descriptors the catalog lists) and how to read each of them. Devices differ only in this data, so there is one factory for all.
/// </summary>
/// <param name="AdapterId">The adapter ID, which is also the device ID in the catalog.</param>
/// <param name="DisplayName">The name used in log lines and messages.</param>
/// <param name="MissingMessage">What the user is told when none of the device's transports is present.</param>
/// <param name="Routes">The transports in order of preference.</param>
/// <param name="CreateRoutes">Creates the route implementations of one adapter, so per-device state lives in its own instances.</param>
public sealed record RoutedDeviceProfile(
    string AdapterId,
    string DisplayName,
    string MissingMessage,
    IReadOnlyList<DeviceRouteDescriptor> Routes,
    Func<RouteContext, IReadOnlyDictionary<DeviceRouteKind, IBatteryRoute>> CreateRoutes);

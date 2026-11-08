using System.Diagnostics;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// A battery adapter for a device that can be reached over several transports. It owns no transport code: per poll it asks the
/// <see cref="IDeviceRouteTable"/> for the routes discovery resolved and tries them through the <see cref="IBatteryRoute"/> of their kind.
/// Each device decides independently, so Batterio never forces two devices onto the same transport.
/// </summary>
public sealed class RoutedBatteryAdapter : IBatteryAdapter
{
    private readonly string _deviceId;
    private readonly string _displayName;
    private readonly IReadOnlyDictionary<DeviceRouteKind, IBatteryRoute> _routes;
    private readonly IDeviceRouteTable _table;
    private readonly IBatterioLog _log;
    private readonly string? _devicePath;
    private readonly CancellationTokenSource _lifetime = new();
    private string? _lastGoodKey;
    private int _disposed;

    /// <param name="devicePath">Optional <c>devicePath</c> setting: only routes whose endpoint path equals it are used.</param>
    public RoutedBatteryAdapter(
        string deviceId,
        string displayName,
        IReadOnlyDictionary<DeviceRouteKind, IBatteryRoute> routes,
        IDeviceRouteTable table,
        IBatterioLog? log = null,
        string? devicePath = null)
    {
        _deviceId = deviceId ?? throw new ArgumentNullException(nameof(deviceId));
        _displayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _log = log ?? new NullBatterioLog();
        _devicePath = devicePath;
    }

    public async Task<BatteryReading> GetBatteryStatus(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var started = Stopwatch.GetTimestamp();
        string used = "no route";
        try
        {
            var (reading, route) = await QueryAsync(attempt.Token).ConfigureAwait(false);
            used = $"{route.Kind} route '{route.EndpointPath}'";
            return reading;
        }
        catch (BatteryAdapterException error)
        {
            _log.Log(BatterioLogLevel.Warning, $"{_displayName} query failed ({error.Kind}): {error.Message}");
            throw;
        }
        finally
        {
            _log.Log(BatterioLogLevel.Information,
                $"{_displayName} query via {used} finished in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
        }
    }

    private async Task<(BatteryReading Reading, ResolvedRoute Route)> QueryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var routes = _table.GetRoutes(_deviceId)
            .Where(route => _devicePath is null || string.Equals(route.EndpointPath, _devicePath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (routes.Length == 0)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.ReceiverMissing, _devicePath is null
                ? $"No Bluetooth connection to {_displayName} and no Logi Bolt receiver with it paired was found."
                : $"The configured HID++ endpoint path '{_devicePath}' was not found.");
        }

        var attempts = Order(routes);
        var offline = new List<BatteryAdapterException>();
        BatteryAdapterException? failure = null;
        foreach (var route in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_routes.TryGetValue(route.Kind, out var implementation))
            {
                continue;
            }

            try
            {
                var reading = await implementation.ReadAsync(route, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _lastGoodKey, route.Key);
                return (reading, route);
            }
            catch (BatteryAdapterException exception) when (exception.Kind == BatteryAdapterErrorKind.DeviceOffline)
            {
                offline.Add(exception);
                _log.Log(BatterioLogLevel.Trace, $"{_displayName} did not answer over {route.Kind} '{route.EndpointPath}': {exception.Message}");
            }
            catch (BatteryAdapterException exception)
            {
                // Another transport may still hold the device.
                failure ??= exception;
                _log.Log(BatterioLogLevel.Warning, $"{route.Kind} '{route.EndpointPath}' could not be used for {_displayName}: {exception.Message}");
            }
        }

        // An unusable route (access failure, protocol error) is worth more to the user than "offline" from the others.
        Volatile.Write(ref _lastGoodKey, null);
        if (failure is not null)
        {
            throw failure;
        }

        throw offline.Count > 0
            ? new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline, string.Join(" ", offline.Select(error => error.Message).Distinct()))
            : new BatteryAdapterException(BatteryAdapterErrorKind.ReceiverMissing, $"No usable route to {_displayName} was found.");
    }

    /// <summary>
    /// Online routes first, then those whose state is unknown; within each group the route that worked last time comes first.
    /// Routes reported offline are skipped unless nothing else is left, because the report may be stale.
    /// </summary>
    private IEnumerable<ResolvedRoute> Order(IReadOnlyList<ResolvedRoute> routes)
    {
        var lastGood = Volatile.Read(ref _lastGoodKey);
        var ordered = routes
            .Select((route, position) => (route, position))
            .OrderBy(entry => entry.route.State switch { RouteState.Online => 0, RouteState.Unknown => 1, _ => 2 })
            .ThenBy(entry => entry.route.Key == lastGood ? 0 : 1)
            .ThenBy(entry => entry.position)
            .Select(entry => entry.route)
            .ToArray();
        var usable = ordered.Where(route => route.State != RouteState.Offline).ToArray();
        return usable.Length > 0 ? usable : ordered;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _lifetime.Cancel();
        return ValueTask.CompletedTask;
    }
}

namespace Batterio.Core;

public sealed class RoutesChangedEventArgs(string deviceId) : EventArgs
{
    public string DeviceId { get; } = deviceId;
}

/// <summary>The routes discovery currently resolved for each device. Written by discovery, read by adapters on every poll.</summary>
public interface IDeviceRouteTable
{
    IReadOnlyList<ResolvedRoute> GetRoutes(string deviceId);

    event EventHandler<RoutesChangedEventArgs>? RoutesChanged;
}

/// <summary>
/// Thread-safe map of device ID to its current routes. Routes change at runtime (a device switches transport, sleeps, a receiver
/// is unplugged) while the polling workers live for the whole session, so adapters look the routes up on every poll.
/// </summary>
public sealed class DeviceRouteTable : IDeviceRouteTable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IReadOnlyList<ResolvedRoute>> _routes = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<RoutesChangedEventArgs>? RoutesChanged;

    public IReadOnlyList<ResolvedRoute> GetRoutes(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        lock (_gate)
        {
            return _routes.TryGetValue(deviceId, out var routes) ? routes : Array.Empty<ResolvedRoute>();
        }
    }

    /// <summary>Replaces the routes of a device. Returns whether they changed; raises <see cref="RoutesChanged"/> if so.</summary>
    public bool SetRoutes(string deviceId, IReadOnlyList<ResolvedRoute> routes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(routes);
        var next = routes.ToArray();
        lock (_gate)
        {
            var previous = _routes.TryGetValue(deviceId, out var existing) ? existing : Array.Empty<ResolvedRoute>();
            // The collection is looked up afresh by every scan (its report-ID set is a new object each time), so only the
            // identity and state of a route decide whether the routes changed.
            if (previous.Count == next.Length &&
                previous.Zip(next).All(pair => pair.First.Key == pair.Second.Key && pair.First.State == pair.Second.State))
            {
                if (next.Length > 0)
                {
                    _routes[deviceId] = next; // keep the freshest collection info without announcing a change
                }

                return false;
            }

            if (next.Length == 0)
            {
                _routes.Remove(deviceId);
            }
            else
            {
                _routes[deviceId] = next;
            }
        }

        try
        {
            RoutesChanged?.Invoke(this, new RoutesChangedEventArgs(deviceId));
        }
        catch
        {
            // A listener must not break discovery.
        }

        return true;
    }
}

namespace Batterio.Core;

/// <summary>Reads a device's battery through one kind of transport.</summary>
public interface IBatteryRoute
{
    /// <exception cref="Batterio.Core.Abstractions.BatteryAdapterException">
    /// <see cref="Batterio.Core.Abstractions.BatteryAdapterErrorKind.DeviceOffline"/> when the device does not answer.
    /// </exception>
    Task<BatteryReading> ReadAsync(ResolvedRoute route, CancellationToken cancellationToken);
}

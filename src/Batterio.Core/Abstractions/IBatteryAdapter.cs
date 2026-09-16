namespace Batterio.Core.Abstractions;

public interface IBatteryAdapter : IAsyncDisposable
{
    Task<BatteryReading> GetBatteryStatus(CancellationToken cancellationToken = default);
}

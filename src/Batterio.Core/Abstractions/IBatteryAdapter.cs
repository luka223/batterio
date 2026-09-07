namespace Batterio.Core.Abstractions;

public interface IBatteryAdapter : IAsyncDisposable
{
    Task<int> GetBatteryStatus(CancellationToken cancellationToken = default);
}

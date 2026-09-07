namespace Batterio.Core.Abstractions;

public interface ITrayService : IAsyncDisposable
{
    event EventHandler<TrayAction>? ActionRaised;

    event EventHandler? SystemResumed;

    TrayIconPlacement? GetIconPlacement();

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

public readonly record struct TrayIconPlacement(
    int IconLeft,
    int IconTop,
    int IconRight,
    int IconBottom,
    int WorkAreaLeft,
    int WorkAreaTop,
    int WorkAreaRight,
    int WorkAreaBottom);

namespace Batterio.Core.Abstractions;

/// <summary>
/// Delivers application-level battery notifications.
/// </summary>
public interface IBatteryNotificationService : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task ShowLowBatteryAsync(string deviceId, string deviceName, CancellationToken cancellationToken = default);

    Task ShowFullyChargedAsync(string deviceId, string deviceName, CancellationToken cancellationToken = default);

    ValueTask IAsyncDisposable.DisposeAsync() => ValueTask.CompletedTask;
}

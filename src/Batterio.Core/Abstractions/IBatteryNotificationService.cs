namespace Batterio.Core.Abstractions;

/// <summary>
/// Delivers an application-level low-battery notification.
/// </summary>
public interface IBatteryNotificationService : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task ShowLowBatteryAsync(string deviceId, string deviceName, CancellationToken cancellationToken = default);

    ValueTask IAsyncDisposable.DisposeAsync() => ValueTask.CompletedTask;
}

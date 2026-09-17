using System.Threading.Channels;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Notifications;

/// <summary>
/// Turns successful battery readings into at-most-once-per-run notification attempts.
/// </summary>
public sealed class BatteryNotificationCoordinator : IAsyncDisposable
{
    public const int LowBatteryThreshold = 30;

    private readonly PollingCoordinator _polling;
    private readonly IBatteryNotificationService _notifications;
    private readonly IBatterioLog _log;
    private readonly object _gate = new();
    private readonly HashSet<string> _lowBatteryNotifiedDeviceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _fullyChargedNotifiedDeviceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<NotificationRequest> _queue = Channel.CreateUnbounded<NotificationRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _stopping = new();
    private Task? _consumer;
    private Task? _startTask;
    private Task? _stopTask;
    private bool _accepting;
    private bool _disposed;

    public BatteryNotificationCoordinator(
        PollingCoordinator polling,
        IBatteryNotificationService notifications,
        IBatterioLog? log = null)
    {
        _polling = polling ?? throw new ArgumentNullException(nameof(polling));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _log = log ?? new NullBatterioLog();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _startTask ??= InitializeAsync(cancellationToken);
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _notifications.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _log.Log(BatterioLogLevel.Warning, "Windows notifications are unavailable for this Batterio run.", exception);
            return;
        }

        lock (_gate)
        {
            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            _consumer = ConsumeAsync();
            _accepting = true;
            _polling.StateChanged += OnStateChanged;
        }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_stopTask is not null) return _stopTask;
            _disposed = true;
            _accepting = false;
            _polling.StateChanged -= OnStateChanged;
            _queue.Writer.TryComplete();
            _stopping.Cancel();
            return _stopTask = StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        // Initialization may still be finishing; do not dispose native resources underneath it.
        if (_startTask is not null)
        {
            try { await _startTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        if (_consumer is not null)
        {
            try { await _consumer.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        try { await _notifications.DisposeAsync().ConfigureAwait(false); }
        finally { _stopping.Dispose(); }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private void OnStateChanged(object? sender, DeviceBatteryStateChangedEventArgs args)
    {
        var state = args.State;
        var percentage = state.Percentage;
        if (!state.Configuration.Enabled || state.Error is not null || percentage is null)
        {
            return;
        }

        var kind = percentage.Value < LowBatteryThreshold
            ? NotificationKind.LowBattery
            : percentage.Value == 100
                ? NotificationKind.FullyCharged
                : (NotificationKind?)null;
        if (kind is null)
        {
            return;
        }

        lock (_gate)
        {
            var notifiedDeviceIds = kind.Value switch
            {
                NotificationKind.LowBattery => _lowBatteryNotifiedDeviceIds,
                NotificationKind.FullyCharged => _fullyChargedNotifiedDeviceIds,
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            if (!_accepting || !notifiedDeviceIds.Add(state.Configuration.Id))
            {
                return;
            }

            if (!_queue.Writer.TryWrite(new NotificationRequest(state.Configuration.Id, state.Configuration.Name, kind.Value)))
            {
                _log.Log(BatterioLogLevel.Warning,
                    $"A {ToLogName(kind.Value)} notification could not be queued for device '{state.Configuration.Id}'.");
            }
        }
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
            {
                try
                {
                    switch (request.Kind)
                    {
                        case NotificationKind.LowBattery:
                            await _notifications.ShowLowBatteryAsync(request.DeviceId, request.DeviceName, _stopping.Token).ConfigureAwait(false);
                            break;
                        case NotificationKind.FullyCharged:
                            await _notifications.ShowFullyChargedAsync(request.DeviceId, request.DeviceName, _stopping.Token).ConfigureAwait(false);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(request.Kind));
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !_stopping.IsCancellationRequested)
                {
                    _log.Log(BatterioLogLevel.Warning,
                        $"Could not display {ToLogName(request.Kind)} notification for device '{request.DeviceId}'.", exception);
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
    }

    private enum NotificationKind
    {
        LowBattery,
        FullyCharged
    }

    private sealed record NotificationRequest(string DeviceId, string DeviceName, NotificationKind Kind);

    private static string ToLogName(NotificationKind kind) => kind switch
    {
        NotificationKind.LowBattery => "low-battery",
        NotificationKind.FullyCharged => "fully-charged",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

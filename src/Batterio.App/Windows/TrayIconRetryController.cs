namespace Batterio.App.Windows;

/// <summary>Deterministic tray registration backoff, independent of Win32 calls.</summary>
public sealed class TrayIconRetryController
{
    private static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(1);
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset? _lastFailureWarning;
    private bool _retryScheduled;

    public TrayIconRetryController(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static IReadOnlyList<TimeSpan> BackoffDelays { get; } =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    public int FailureCount { get; private set; }

    public bool ShouldLogFailure() => ShouldLogFailure(_timeProvider.GetUtcNow());

    public bool ShouldLogFailure(DateTimeOffset now)
    {
        if (_lastFailureWarning.HasValue && now - _lastFailureWarning.Value < WarningInterval)
        {
            return false;
        }

        _lastFailureWarning = now;
        return true;
    }

    public void MarkRetryScheduled() => _retryScheduled = true;

    public bool CancelRetry()
    {
        if (!_retryScheduled)
        {
            return false;
        }

        _retryScheduled = false;
        return true;
    }

    public TimeSpan RecordFailure()
    {
        var index = Math.Min(FailureCount, BackoffDelays.Count - 1);
        FailureCount++;
        return BackoffDelays[index];
    }

    public void Reset()
    {
        FailureCount = 0;
        _retryScheduled = false;
    }

    public void ResetFailureWarning() => _lastFailureWarning = null;
}

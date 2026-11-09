using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// A long-lived, listen-only reader on the Keychron Link dongle's raw HID collection. The custom firmware pushes a battery report about
/// every two seconds whether anyone listens or not, so listening all the time is the only wake-up signal the keyboard has.
/// It keeps the newest report and the time it arrived, and never writes to the dongle.
/// </summary>
/// <remarks>
/// The route is <see cref="RouteState.Online"/> while reports arrive, <see cref="RouteState.Offline"/> after <see cref="SilenceTimeout"/>
/// without one, and <see cref="RouteState.Unknown"/> before the first report and after the listener failed. The read loop reuses one
/// buffer, so an arriving report costs a 33-byte copy and no allocation.
/// Going Offline after the silence makes the keyboard show as Offline about 3 s later
/// (<see cref="DeviceSelectionCoordinator.DefaultOfflineGrace"/>), about 9 s after its last report. <see cref="KeychronLinkRoute.CachedFrameMaxAge"/>
/// must stay below <see cref="SilenceTimeout"/>, or a read after that mark could return a stale cached report.
/// The idle report cadence has not been measured yet: run <c>HardwareSmokeTests.ListenForKeychronReports</c> and raise
/// <see cref="SilenceTimeout"/> if the longest gap while awake comes close to it.
/// </remarks>
public sealed class KeychronLinkListener : IAsyncDisposable
{
    public static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(6);

    private readonly IHidCollectionProvider _provider;
    private readonly HidCollectionInfo _collection;
    private readonly TimeProvider _timeProvider;
    private readonly IBatterioLog _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private ITimer? _silenceTimer;
    private Task? _loop;
    private TaskCompletionSource _frameArrived = NewSignal();
    private byte[] _frame = [];
    private DateTimeOffset _frameAt;
    private bool _hasFrame;
    private RouteState _state = RouteState.Unknown;
    private bool _closed;
    private int _disposed;

    public KeychronLinkListener(
        IHidCollectionProvider provider,
        HidCollectionInfo collection,
        TimeProvider? timeProvider = null,
        IBatterioLog? log = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _log = log ?? new NullBatterioLog();
    }

    public string Path => _collection.DevicePath;

    /// <summary>Raised, from the listener's thread, when <see cref="State"/> or <see cref="IsClosed"/> changed.</summary>
    public event EventHandler? StateChanged;

    public RouteState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>The listener stopped (read failure or disposal) and must be replaced to listen again.</summary>
    public bool IsClosed
    {
        get
        {
            lock (_gate)
            {
                return _closed;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null || _closed)
            {
                return;
            }

            _silenceTimer = _timeProvider.CreateTimer(OnSilence, null, SilenceTimeout, Timeout.InfiniteTimeSpan);
            _loop = Task.Run(() => RunAsync(_lifetime.Token));
        }
    }

    /// <summary>A copy of the newest report if it arrived no longer than <paramref name="maxAge"/> ago.</summary>
    public byte[]? TryGetFrame(TimeSpan maxAge)
    {
        lock (_gate)
        {
            return _hasFrame && _timeProvider.GetUtcNow() - _frameAt <= maxAge ? (byte[])_frame.Clone() : null;
        }
    }

    /// <summary>
    /// The newest report if it is at most <paramref name="maxAge"/> old, else the next one to arrive within <paramref name="timeout"/>.
    /// Null if none arrived in time, or the listener closed meanwhile (see <see cref="IsClosed"/>).
    /// </summary>
    public async Task<byte[]?> WaitForFrameAsync(TimeSpan maxAge, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var expired = Task.Delay(timeout, _timeProvider, stop.Token);
        try
        {
            while (true)
            {
                Task signal;
                lock (_gate)
                {
                    if (_hasFrame && _timeProvider.GetUtcNow() - _frameAt <= maxAge)
                    {
                        return (byte[])_frame.Clone();
                    }

                    if (_closed)
                    {
                        return null;
                    }

                    signal = _frameArrived.Task;
                }

                if (await Task.WhenAny(signal, expired).ConfigureAwait(false) == expired)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return null;
                }
            }
        }
        finally
        {
            stop.Cancel();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var transport = await _provider.OpenAsync(_collection, cancellationToken).ConfigureAwait(false);
            await using var transaction = await transport.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[Math.Max(_collection.InputReportLength, KeychronBatteryReportDecoding.MinimumReportLength)];
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = await transaction.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                // Other raw HID traffic is ignored; only the battery report says the keyboard is there.
                if (KeychronBatteryReportDecoding.IsQ1HeBatteryReport(buffer.AsSpan(0, count)))
                {
                    OnFrame(buffer.AsSpan(0, count));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Warning, $"Listening to the Keychron Link receiver '{Path}' stopped: {exception.Message}");
        }
        finally
        {
            Close();
        }
    }

    private void OnFrame(ReadOnlySpan<byte> frame)
    {
        TaskCompletionSource signal;
        bool changed;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            if (_frame.Length != frame.Length)
            {
                _frame = new byte[frame.Length];
            }

            frame.CopyTo(_frame);
            _frameAt = _timeProvider.GetUtcNow();
            _hasFrame = true;
            changed = _state != RouteState.Online;
            _state = RouteState.Online;
            signal = _frameArrived;
            _frameArrived = NewSignal();
            _silenceTimer?.Change(SilenceTimeout, Timeout.InfiniteTimeSpan);
        }

        signal.TrySetResult();
        if (changed)
        {
            RaiseStateChanged();
        }
    }

    private void OnSilence(object? state)
    {
        lock (_gate)
        {
            if (_closed || _state == RouteState.Offline)
            {
                return;
            }

            _state = RouteState.Offline;
        }

        RaiseStateChanged();
    }

    private void Close()
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _state = RouteState.Unknown;
            signal = _frameArrived;
            _frameArrived = NewSignal();
            _silenceTimer?.Dispose();
            _silenceTimer = null;
        }

        // Wake the readers that wait for a report so they notice the listener is gone.
        signal.TrySetResult();
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // A listener of the event must not break the read loop.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Task? loop;
        lock (_gate)
        {
            loop = _loop;
        }

        _lifetime.Cancel();
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch
            {
                // The loop reports its own failures.
            }
        }

        Close();
        _lifetime.Dispose();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

using Batterio.Core.Abstractions;

namespace Batterio.Core;

public enum TrayNotification
{
    LeftButtonUp,
    LeftButtonDoubleClick,
    KeyboardActivate,
    RightButtonUp
}

public enum TrayAction
{
    TogglePopup,
    ShowPopup,
    ShowMenu
}

public sealed class TrayClickArbiter : IAsyncDisposable
{
    private readonly IClickDelay _delay;
    private readonly IDoubleClickTimeProvider _doubleClickTime;
    private readonly object _gate = new();
    private CancellationTokenSource? _singleClickCancellation;
    private bool _suppressNextButtonUp;
    private bool _disposed;

    public TrayClickArbiter(IClickDelay delay, IDoubleClickTimeProvider doubleClickTime)
    {
        _delay = delay;
        _doubleClickTime = doubleClickTime;
    }

    public event EventHandler<TrayAction>? ActionRaised;

    public Task HandleAsync(TrayNotification notification)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            return notification switch
            {
                TrayNotification.LeftButtonUp => ScheduleSingleClickAsync(),
                TrayNotification.LeftButtonDoubleClick => HandleDoubleClickAsync(),
                TrayNotification.KeyboardActivate => RaiseImmediateAsync(TrayAction.TogglePopup),
                TrayNotification.RightButtonUp => RaiseImmediateAsync(TrayAction.ShowMenu),
                _ => Task.CompletedTask
            };
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            _singleClickCancellation?.Cancel();
            _singleClickCancellation?.Dispose();
            _singleClickCancellation = null;
        }

        return ValueTask.CompletedTask;
    }

    private Task ScheduleSingleClickAsync()
    {
        if (_suppressNextButtonUp)
        {
            _suppressNextButtonUp = false;
            return Task.CompletedTask;
        }

        _singleClickCancellation?.Cancel();
        _singleClickCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _singleClickCancellation = cancellation;
        return RunDelayedSingleClickAsync(cancellation);
    }

    private async Task RunDelayedSingleClickAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await _delay.Delay(_doubleClickTime.GetDoubleClickTime(), cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (!ReferenceEquals(_singleClickCancellation, cancellation) || _disposed)
                {
                    return;
                }

                _singleClickCancellation = null;
            }

            Raise(TrayAction.TogglePopup);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private Task HandleDoubleClickAsync()
    {
        _singleClickCancellation?.Cancel();
        _singleClickCancellation?.Dispose();
        _singleClickCancellation = null;
        _suppressNextButtonUp = true;
        Raise(TrayAction.ShowPopup);
        return Task.CompletedTask;
    }

    private Task RaiseImmediateAsync(TrayAction action)
    {
        Raise(action);
        return Task.CompletedTask;
    }

    private void Raise(TrayAction action) => ActionRaised?.Invoke(this, action);
}


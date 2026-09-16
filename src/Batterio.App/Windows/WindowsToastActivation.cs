namespace Batterio.App.Windows;

/// <summary>Coalesces early activations until the popup and application services are ready.</summary>
public sealed class WindowsToastActivation
{
    public static WindowsToastActivation Current { get; } = new();
    private readonly object _gate = new();
    private Action? _dispatchPopup;
    private bool _pending;
    private bool _stopped;

    public void RequestPopup()
    {
        lock (_gate)
        {
            if (_stopped) return;
            if (_dispatchPopup is null) _pending = true;
            else _dispatchPopup();
        }
    }

    public void SetReady(Action dispatchPopup)
    {
        ArgumentNullException.ThrowIfNull(dispatchPopup);
        lock (_gate)
        {
            if (_stopped) return;
            _dispatchPopup = dispatchPopup;
            if (!_pending) return;
            _pending = false;
            _dispatchPopup();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            _pending = false;
            _dispatchPopup = null;
        }
    }
}

namespace Batterio.App.Windows;

/// <summary>Coalesces early activations until the popup and application services are ready.</summary>
public sealed class WindowsToastActivation
{
    public static WindowsToastActivation Current { get; } = new();
    private readonly object _gate = new();
    private Action? _dispatchPopup;
    private Action? _dispatchSettings;
    private bool _pending;
    private bool _settingsPending;
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

    /// <summary>Opens Settings, for example from the update notification.</summary>
    public void RequestSettings()
    {
        lock (_gate)
        {
            if (_stopped) return;
            if (_dispatchSettings is null) _settingsPending = true;
            else _dispatchSettings();
        }
    }

    public void SetReady(Action dispatchPopup, Action? dispatchSettings = null)
    {
        ArgumentNullException.ThrowIfNull(dispatchPopup);
        lock (_gate)
        {
            if (_stopped) return;
            _dispatchPopup = dispatchPopup;
            _dispatchSettings = dispatchSettings;
            if (_settingsPending && _dispatchSettings is not null)
            {
                _settingsPending = false;
                _dispatchSettings();
            }

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
            _settingsPending = false;
            _dispatchPopup = null;
            _dispatchSettings = null;
        }
    }
}

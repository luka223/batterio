namespace Batterio.Core.Abstractions;

/// <summary>Tells discovery that HID devices were plugged in or removed, so it can rescan without waiting for the fallback scan.</summary>
public interface IDeviceChangeNotifier : IDisposable
{
    /// <summary>A HID interface arrived or was removed. May be raised on any thread and in bursts.</summary>
    event EventHandler? Changed;

    /// <summary>Starts listening. Safe to call more than once.</summary>
    void Start();
}

/// <summary>Never reports a change; for tests and platforms without device notifications. Discovery then relies on its fallback scan.</summary>
public sealed class NullDeviceChangeNotifier : IDeviceChangeNotifier
{
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public void Start()
    {
    }

    public void Dispose()
    {
    }
}

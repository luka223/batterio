namespace Batterio.Core.Abstractions;

public enum BluetoothLinkState
{
    /// <summary>The state cannot be determined; the route is read to find out.</summary>
    Unknown,
    Connected,
    Disconnected
}

/// <summary>Reports whether a Bluetooth LE HID device is currently connected, and when that changes.</summary>
public interface IBluetoothConnectionMonitor : IDisposable
{
    /// <summary>
    /// The connection state of the device behind <paramref name="hidDevicePath"/>. Returns <see cref="BluetoothLinkState.Unknown"/>
    /// when it is not known (yet); <see cref="StatusChanged"/> is raised once it becomes known or changes.
    /// </summary>
    BluetoothLinkState GetState(string hidDevicePath);

    /// <summary>The connection state of some device changed. May be raised on any thread.</summary>
    event EventHandler? StatusChanged;

    /// <summary>Starts monitoring. Safe to call more than once.</summary>
    void Start();
}

/// <summary>Knows nothing about any connection; routes stay <see cref="BluetoothLinkState.Unknown"/> and are probed by a ping.</summary>
public sealed class NullBluetoothConnectionMonitor : IBluetoothConnectionMonitor
{
    public BluetoothLinkState GetState(string hidDevicePath) => BluetoothLinkState.Unknown;

    public event EventHandler? StatusChanged
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

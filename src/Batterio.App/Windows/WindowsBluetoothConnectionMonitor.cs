using System.Collections.Concurrent;
using Batterio.Core;
using Batterio.Core.Abstractions;
using Windows.Devices.Bluetooth;

namespace Batterio.App.Windows;

/// <summary>
/// Tells whether a Bluetooth LE HID device is connected, and raises <see cref="StatusChanged"/> when that changes, so that a mouse
/// that wakes up or is switched back to this computer is read at once. The device is found through the Bluetooth address in
/// its HID path. If the address cannot be parsed or the device cannot be opened the state stays
/// <see cref="BluetoothLinkState.Unknown"/> and the route is probed by a ping, as without a monitor.
/// </summary>
public sealed class WindowsBluetoothConnectionMonitor : IBluetoothConnectionMonitor
{
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<ulong, Tracked> _devices = new();
    private readonly Action<string>? _logWarning;
    private int _disposed;

    public WindowsBluetoothConnectionMonitor(Action<string>? logWarning = null)
    {
        _logWarning = logWarning;
    }

    public event EventHandler? StatusChanged;

    public void Start()
    {
    }

    public BluetoothLinkState GetState(string hidDevicePath)
    {
        if (Volatile.Read(ref _disposed) != 0 || !HidCollectionPaths.TryParseBluetoothAddress(hidDevicePath, out var address))
        {
            return BluetoothLinkState.Unknown;
        }

        var tracked = _devices.GetOrAdd(address, _ => new Tracked());
        var device = tracked.Device;
        if (device is not null)
        {
            return ToState(device.ConnectionStatus);
        }

        if (tracked.TryBeginLookup(RetryAfterFailure))
        {
            _ = TrackAsync(address, tracked);
        }

        return BluetoothLinkState.Unknown;
    }

    private async Task TrackAsync(ulong address, Tracked tracked)
    {
        try
        {
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (device is null)
            {
                tracked.EndLookup(null);
                return;
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                device.Dispose();
                return;
            }

            device.ConnectionStatusChanged += OnConnectionStatusChanged;
            tracked.EndLookup(device);
            RaiseStatusChanged(); // the state is known now
        }
        catch (Exception exception)
        {
            tracked.EndLookup(null);
            _logWarning?.Invoke($"Bluetooth connection status of {address:X12} could not be monitored: {exception.Message}");
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args) => RaiseStatusChanged();

    private void RaiseStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // A listener must not break the Bluetooth callback.
        }
    }

    private static BluetoothLinkState ToState(BluetoothConnectionStatus status) =>
        status == BluetoothConnectionStatus.Connected ? BluetoothLinkState.Connected : BluetoothLinkState.Disconnected;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var tracked in _devices.Values)
        {
            if (tracked.Device is { } device)
            {
                device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                device.Dispose();
            }
        }

        _devices.Clear();
    }

    private sealed class Tracked
    {
        private readonly object _gate = new();
        private bool _looking;
        private DateTime _failedAt = DateTime.MinValue;
        private BluetoothLEDevice? _device;

        public BluetoothLEDevice? Device
        {
            get
            {
                lock (_gate)
                {
                    return _device;
                }
            }
        }

        public bool TryBeginLookup(TimeSpan retryAfter)
        {
            lock (_gate)
            {
                if (_looking || _device is not null || DateTime.UtcNow - _failedAt < retryAfter)
                {
                    return false;
                }

                _looking = true;
                return true;
            }
        }

        public void EndLookup(BluetoothLEDevice? device)
        {
            lock (_gate)
            {
                _looking = false;
                _device = device;
                if (device is null)
                {
                    _failedAt = DateTime.UtcNow;
                }
            }
        }
    }
}

using System.Runtime.InteropServices;
using Batterio.Core.Abstractions;

namespace Batterio.App.Windows;

/// <summary>
/// Raises <see cref="Changed"/> when a HID interface arrives or is removed, using a Configuration Manager notification on the HID
/// device interface class. Plugging in a Logi Bolt receiver or a USB device therefore causes a rescan within moments.
/// </summary>
/// <remarks>
/// Hand-written P/Invoke, like <see cref="WindowsHidCollectionProvider"/>. The callback runs on a system thread; it only raises an
/// event, and discovery debounces the bursts a device produces (one notification per HID collection).
/// </remarks>
public sealed class WindowsDeviceChangeNotifier : IDeviceChangeNotifier
{
    private const uint CrSuccess = 0;
    private const int NotifyFilterTypeDeviceInterface = 0;
    private static readonly Guid HidInterfaceClass = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    private readonly object _gate = new();
    private readonly CmNotifyCallback _callback;
    private readonly Action<string>? _logWarning;
    private nint _registration;
    private bool _disposed;

    public WindowsDeviceChangeNotifier(Action<string>? logWarning = null)
    {
        _logWarning = logWarning;
        _callback = OnNotification;
    }

    public event EventHandler? Changed;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _registration != nint.Zero)
            {
                return;
            }

            var filter = new CmNotifyFilter
            {
                Size = Marshal.SizeOf<CmNotifyFilter>(),
                FilterType = NotifyFilterTypeDeviceInterface,
                ClassGuid = HidInterfaceClass
            };
            var result = CM_Register_Notification(ref filter, nint.Zero, _callback, out var registration);
            if (result != CrSuccess)
            {
                // Discovery still finds devices with its fallback scan.
                _logWarning?.Invoke($"Device change notifications could not be registered (CONFIGRET {result}).");
                return;
            }

            _registration = registration;
        }
    }

    private uint OnNotification(nint notify, nint context, int action, nint eventData, int eventDataSize)
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // A listener must never break the notification thread.
        }

        return CrSuccess;
    }

    public void Dispose()
    {
        nint registration;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            registration = _registration;
            _registration = nint.Zero;
        }

        if (registration != nint.Zero)
        {
            _ = CM_Unregister_Notification(registration);
        }
    }

    // CM_NOTIFY_FILTER: four DWORDs followed by a union of GUID / HANDLE / WCHAR[MAX_DEVICE_ID_LEN = 200], 416 bytes in total.
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    private struct CmNotifyFilter
    {
        [FieldOffset(0)] public int Size;
        [FieldOffset(4)] public int Flags;
        [FieldOffset(8)] public int FilterType;
        [FieldOffset(12)] public int Reserved;
        [FieldOffset(16)] public Guid ClassGuid;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint CmNotifyCallback(nint notify, nint context, int action, nint eventData, int eventDataSize);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Register_Notification(ref CmNotifyFilter filter, nint context, CmNotifyCallback callback, out nint notifyContext);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Unregister_Notification(nint notifyContext);
}

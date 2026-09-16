using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Batterio.App.Windows;

/// <summary>Keeps the class factory in a live MTA until notification shutdown.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsToastComServer : IDisposable
{
    private readonly ManualResetEventSlim _stop = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private int _disposed;

    public WindowsToastComServer(Guid activatorId, Action<string, string?> activated)
    {
        _thread = new Thread(() => Run(activatorId, activated)) { IsBackground = true, Name = "Batterio toast activator" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        try { _ready.Task.GetAwaiter().GetResult(); }
        catch { Dispose(); throw; }
    }

    private void Run(Guid activatorId, Action<string, string?> activated)
    {
        uint cookie = 0;
        var initialized = false;
        try
        {
            Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 0));
            initialized = true;
            var factory = new ToastClassFactory(activated);
            // CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE: Windows can reuse the running instance.
            Marshal.ThrowExceptionForHR(CoRegisterClassObject(ref activatorId, factory, 4, 1, out cookie));
            _ready.TrySetResult();
            _stop.Wait();
            GC.KeepAlive(factory);
        }
        catch (Exception exception) { _ready.TrySetException(exception); }
        finally
        {
            if (cookie != 0) CoRevokeClassObject(cookie);
            if (initialized) CoUninitialize();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Set();
        _thread.Join();
        _stop.Dispose();
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoRegisterClassObject(ref Guid clsid,
        [MarshalAs(UnmanagedType.Interface)] IToastClassFactory factory, uint context, uint flags, out uint cookie);
    [DllImport("ole32.dll")] private static extern int CoRevokeClassObject(uint cookie);
}

[ComVisible(true), Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IToastClassFactory
{
    [PreserveSig] int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr instance);
    [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool locked);
}

[ComVisible(true), Guid("53E31837-6600-4A81-9395-75CFFE746F94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IToastActivationCallback
{
    void Activate([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [MarshalAs(UnmanagedType.LPWStr)] string? invokedArgs, IntPtr userInput, uint count);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
[SupportedOSPlatform("windows")]
public sealed class ToastClassFactory(Action<string, string?> activated) : IToastClassFactory
{
    public int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr instance)
    {
        instance = IntPtr.Zero;
        if (outer != IntPtr.Zero) return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION
        var callback = new ToastActivationCallback(activated);
        var unknown = Marshal.GetIUnknownForObject(callback);
        try { return Marshal.QueryInterface(unknown, in iid, out instance); }
        finally { Marshal.Release(unknown); }
    }

    public int LockServer(bool locked) => 0;
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ToastActivationCallback(Action<string, string?> activated) : IToastActivationCallback
{
    public void Activate(string appUserModelId, string? invokedArgs, IntPtr userInput, uint count) =>
        activated(appUserModelId, invokedArgs);
}

using System.Runtime.InteropServices;
using System.Text;
using Batterio.App.Windows;
using Xunit;

namespace Batterio.Windows.Tests;

/// <summary>Windows-only interop checks; no toast is displayed and no persistent registration is changed.</summary>
public sealed class NativeToastTests
{
    [Fact]
    public void NativeClassFactoryActivatesAndCanBeReleasedRepeatedly()
    {
        var id = Guid.NewGuid();
        string? received = null;
        using (var server = new WindowsToastComServer(id, (appId, arguments) => received = appId + ":" + arguments))
        {
            var iid = typeof(IToastActivationCallback).GUID;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref id, IntPtr.Zero, 4, ref iid, out var pointer));
            try
            {
                var callback = (IToastActivationCallback)Marshal.GetObjectForIUnknown(pointer);
                callback.Activate("test.batterio", "low-battery", IntPtr.Zero, 0);
                Assert.Equal("test.batterio:low-battery", received);
                if (Marshal.IsComObject(callback)) Marshal.FinalReleaseComObject(callback);
            }
            finally { Marshal.Release(pointer); }
            server.Dispose();
            server.Dispose();
        }
        // Re-registering the same id exercises native teardown/reinitialization on a fresh MTA.
        using var next = new WindowsToastComServer(id, (_, _) => { });
    }

    [Fact]
    public void ShortcutPersistsAppIdAndActivatorAndRepairsTarget()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Batterio-toast-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "Batterio.lnk");
        var activator = Guid.NewGuid();
        try
        {
            foreach (var target in new[] { @"C:\First folder\Batterio.exe", @"C:\Moved folder\Batterio.exe" })
            {
                WindowsToastPlatform.WriteShortcutFile(path, new(target, "test.batterio", activator));
                var shellLink = Activator.CreateInstance(Type.GetTypeFromCLSID(new("00021401-0000-0000-C000-000000000046"))!)!;
                try
                {
                    ((System.Runtime.InteropServices.ComTypes.IPersistFile)shellLink).Load(path, 0);
                    var properties = (IPropertyStore)shellLink;
                    Assert.Equal("test.batterio", ReadString(properties, 5));
                    var key = new PropertyKey(26);
                    properties.GetValue(ref key, out var value);
                    try
                    {
                        Assert.Equal(72, value.Type);
                        Assert.Equal(activator, Marshal.PtrToStructure<Guid>(value.Pointer));
                    }
                    finally { PropVariantClear(ref value); }
                    var savedTarget = new StringBuilder(32768);
                    ((IShellLinkPath)shellLink).GetPath(savedTarget, savedTarget.Capacity, IntPtr.Zero, 4);
                    Assert.Equal(target, savedTarget.ToString());
                }
                finally { Marshal.FinalReleaseComObject(shellLink); }
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    private static string? ReadString(IPropertyStore properties, uint pid)
    {
        var key = new PropertyKey(pid);
        properties.GetValue(ref key, out var value);
        try { Assert.Equal(31, value.Type); return Marshal.PtrToStringUni(value.Pointer); }
        finally { PropVariantClear(ref value); }
    }

    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkPath
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, IntPtr findData, uint flags);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, in PropVariant value);
        void Commit();
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid format, uint id)
    {
        public Guid Format = format;
        public uint Id = id;
        public PropertyKey(uint id) : this(new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), id) { }
    }
    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
        [FieldOffset(8)] private CountedPointer _size;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct CountedPointer { public uint Count; public IntPtr Pointer; }
}

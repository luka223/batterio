using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Batterio.App.Windows;

[SupportedOSPlatform("windows10.0.10240")]
public sealed class WindowsToastPlatform : IWindowsToastPlatform
{
    public void SetProcessAppId(string appId) =>
        global::Windows.Win32.PInvoke.SetCurrentProcessExplicitAppUserModelID(appId).ThrowOnFailure();

    public void WriteShortcut(ToastShortcutRegistration registration)
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (string.IsNullOrWhiteSpace(programs))
            throw new InvalidOperationException("The Start Menu Programs folder is unavailable.");
        WriteShortcutFile(Path.Combine(programs, "Batterio.lnk"), registration);
    }

    // Explicit destination also permits native tests to use an isolated temporary shortcut.
    public static void WriteShortcutFile(string shortcutPath, ToastShortcutRegistration registration)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var shellLink = Activator.CreateInstance(Type.GetTypeFromCLSID(new("00021401-0000-0000-C000-000000000046"))!)!;
        try
        {
            var link = (IShellLinkW)shellLink;
            link.SetPath(registration.ExecutablePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(registration.ExecutablePath)!);
            link.SetDescription("Batterio");
            link.SetIconLocation(registration.ExecutablePath, 0);
            var properties = (IPropertyStore)shellLink;
            var appIdKey = new PropertyKey(new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
            var activatorKey = new PropertyKey(appIdKey.FormatId, 26);
            using var appId = PropVariant.FromString(registration.AppUserModelId);
            using var activator = PropVariant.FromGuid(registration.ActivatorId);
            properties.SetValue(ref appIdKey, in appId);
            properties.SetValue(ref activatorKey, in activator);
            properties.Commit();
            ((IPersistFile)shellLink).Save(shortcutPath, true);
        }
        finally { Marshal.FinalReleaseComObject(shellLink); }
    }

    public void WriteComServer(Guid activatorId, string executablePath, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{activatorId:B}\LocalServer32", true)
            ?? throw new InvalidOperationException("Could not register the Batterio toast COM server.");
        key.SetValue(string.Empty, command, RegistryValueKind.String);
        // COM passes this unquoted path as CreateProcess.lpApplicationName. Repair it along
        // with the command so a previous location cannot remain the executable target.
        key.SetValue("ServerExecutable", executablePath, RegistryValueKind.String);
    }

    public IDisposable RegisterActivator(Guid activatorId, Action<string, string?> activated) =>
        new WindowsToastComServer(activatorId, activated);

    public void Show(string appId, string xml, Action<Exception> failed)
    {
        var document = new XmlDocument();
        document.LoadXml(xml);
        var toast = new ToastNotification(document);
        toast.Failed += (_, args) => failed(args.ErrorCode);
        ToastNotificationManager.CreateToastNotifier(appId).Show(toast);
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr windowHandle, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string? fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint propertyCount);
        void GetAt(uint propertyIndex, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, in PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant : IDisposable
    {
        [FieldOffset(0)] private ushort _type;
        [FieldOffset(8)] private IntPtr _pointer;
        // The native union includes counted arrays: 24 bytes on x64, 16 on x86.
        [FieldOffset(8)] private CountedPointer _size;

        public static PropVariant FromString(string value) => new()
        { _type = 31, _pointer = Marshal.StringToCoTaskMemUni(value) };

        public static PropVariant FromGuid(Guid value)
        {
            var pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<Guid>());
            Marshal.StructureToPtr(value, pointer, false);
            return new() { _type = 72, _pointer = pointer };
        }

        public void Dispose()
        {
            Marshal.FreeCoTaskMem(_pointer);
            _pointer = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CountedPointer
        {
            public uint Count;
            public IntPtr Pointer;
        }
    }
}

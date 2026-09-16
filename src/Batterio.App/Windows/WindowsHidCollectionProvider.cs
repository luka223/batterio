using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Batterio.Core;
using Batterio.Core.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace Batterio.App.Windows;

public sealed class WindowsHidCollectionProvider : IHidCollectionProvider
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int HidpStatusSuccess = 0x00110000;
    private static readonly Regex VendorProductRegex = new(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex InterfaceRegex = new(@"&MI_([0-9A-F]{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public IReadOnlyList<HidCollectionInfo> Enumerate(HidCollectionQuery query)
    {
        HidNativeMethods.HidD_GetHidGuid(out var hidGuid);
        var infoSet = HidNativeMethods.SetupDiGetClassDevs(ref hidGuid, null, nint.Zero, DigcfPresent | DigcfDeviceInterface);
        if (infoSet == HidNativeMethods.InvalidHandleValue)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.AccessFailure,
                $"Windows HID collection enumeration failed with error {Marshal.GetLastWin32Error()}.");
        }

        try
        {
            var collections = new List<HidCollectionInfo>();
            var inaccessible = new List<string>();
            for (uint index = 0; ; index++)
            {
                var interfaceData = new HidNativeMethods.DeviceInterfaceData
                {
                    Size = Marshal.SizeOf<HidNativeMethods.DeviceInterfaceData>()
                };
                if (!HidNativeMethods.SetupDiEnumDeviceInterfaces(infoSet, nint.Zero, ref hidGuid, index, ref interfaceData))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == HidNativeMethods.ErrorNoMoreItems)
                    {
                        break;
                    }

                    throw new BatteryAdapterException(BatteryAdapterErrorKind.AccessFailure, $"HID enumeration failed: {error}.");
                }

                var path = GetDevicePath(infoSet, ref interfaceData);
                if (path is null)
                {
                    continue;
                }

                var identity = ParseIdentity(path);
                if (identity is null || identity.Value.VendorId != query.VendorId || identity.Value.ProductId != query.ProductId ||
                    (query.InterfaceNumber.HasValue && identity.Value.InterfaceNumber != query.InterfaceNumber))
                {
                    continue;
                }

                try
                {
                    // Descriptor inspection needs no read/write access to the collection.
                    using var handle = HidNativeMethods.CreateFile(path, 0, FileShareRead | FileShareWrite,
                        nint.Zero, OpenExisting, 0, nint.Zero);
                    if (handle.IsInvalid)
                    {
                        inaccessible.Add($"{path} (CreateFile error {Marshal.GetLastWin32Error()})");
                        continue;
                    }
                    if (!TryGetCaps(handle, out var caps, out var reportIds, out var detailError))
                    {
                        inaccessible.Add($"{path} ({detailError})");
                        continue;
                    }

                    var collection = new HidCollectionInfo
                    {
                        DevicePath = path,
                        VendorId = identity.Value.VendorId,
                        ProductId = identity.Value.ProductId,
                        InterfaceNumber = identity.Value.InterfaceNumber,
                        UsagePage = caps.UsagePage,
                        Usage = caps.Usage,
                        InputReportLength = caps.InputReportByteLength,
                        OutputReportLength = caps.OutputReportByteLength,
                        FeatureReportLength = caps.FeatureReportByteLength,
                        SupportedReportIds = reportIds
                    };

                    if (query.Matches(collection))
                    {
                        collections.Add(collection);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // An inaccessible unrelated collection should not hide an accessible matching collection.
                }
                catch (IOException)
                {
                    // The device may disappear between SetupDi enumeration and CreateFile.
                }
            }

            if (collections.Count == 0 && inaccessible.Count > 0)
                throw new BatteryAdapterException(BatteryAdapterErrorKind.AccessFailure,
                    $"Could not inspect HID descriptors for: {string.Join(", ", inaccessible)}.");
            return collections;
        }
        finally
        {
            HidNativeMethods.SetupDiDestroyDeviceInfoList(infoSet);
        }
    }

    public ValueTask<IHidReportTransport> OpenAsync(HidCollectionInfo collection, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var handle = OpenHandle(collection.DevicePath);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new BatteryAdapterException(BatteryAdapterErrorKind.AccessFailure,
                    $"Could not open HID collection '{collection.DevicePath}' (Windows error {error}).");
            }

            try { return ValueTask.FromResult<IHidReportTransport>(new WindowsHidReportTransport(collection, handle)); }
            catch { handle.Dispose(); throw; }
        }
        catch (BatteryAdapterException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.AccessFailure,
                $"Could not open HID collection '{collection.DevicePath}'.", exception);
        }
    }

    private static SafeFileHandle OpenHandle(string path) =>
        HidNativeMethods.CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
            nint.Zero, OpenExisting, FileFlagOverlapped, nint.Zero);

    private static string? GetDevicePath(nint infoSet, ref HidNativeMethods.DeviceInterfaceData interfaceData)
    {
        _ = HidNativeMethods.SetupDiGetDeviceInterfaceDetail(infoSet, ref interfaceData, nint.Zero, 0, out var required, nint.Zero);
        if (required <= 0)
        {
            return null;
        }

        var detail = Marshal.AllocHGlobal(required);
        try
        {
            Marshal.WriteInt32(detail, nint.Size == 8 ? 8 : 6);
            if (!HidNativeMethods.SetupDiGetDeviceInterfaceDetail(infoSet, ref interfaceData, detail, required, out _, nint.Zero))
            {
                return null;
            }

            return ReadDevicePath(detail);
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    // cbSize is 8 on x64, but DevicePath follows the DWORD at offset 4 on both architectures.
    internal static string? ReadDevicePath(nint detail) => Marshal.PtrToStringUni(detail + sizeof(uint));

    private static (ushort VendorId, ushort ProductId, ushort? InterfaceNumber)? ParseIdentity(string path)
    {
        var match = VendorProductRegex.Match(path);
        if (!match.Success ||
            !ushort.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var vendorId) ||
            !ushort.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.HexNumber, null, out var productId))
        {
            return null;
        }

        var interfaceMatch = InterfaceRegex.Match(path);
        ushort? interfaceNumber = interfaceMatch.Success &&
                                  ushort.TryParse(interfaceMatch.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var value)
            ? value
            : null;
        return (vendorId, productId, interfaceNumber);
    }

    private static bool TryGetCaps(SafeFileHandle handle, out HidNativeMethods.HidpCaps caps, out IReadOnlySet<byte> reportIds, out string error)
    {
        caps = default;
        reportIds = new HashSet<byte>();
        error = "HidD_GetPreparsedData failed";
        if (!HidNativeMethods.HidD_GetPreparsedData(handle, out var preparsedData))
        {
            return false;
        }

        try
        {
            var status = HidNativeMethods.HidP_GetCaps(preparsedData, out caps);
            error = $"HidP_GetCaps status 0x{status:X8}";
            if (status != HidpStatusSuccess) return false;
            var input = ReadReportIds(preparsedData, 0, caps.NumberInputButtonCaps, caps.NumberInputValueCaps, out var inputError);
            var output = ReadReportIds(preparsedData, 1, caps.NumberOutputButtonCaps, caps.NumberOutputValueCaps, out var outputError);
            error = $"{inputError}; {outputError}";
            if (input is null || output is null) return false;
            input.IntersectWith(output);
            reportIds = input;
            return true;
        }
        finally
        {
            HidNativeMethods.HidD_FreePreparsedData(preparsedData);
        }
    }

    private static HashSet<byte>? ReadReportIds(nint preparsed, int reportType, ushort buttonCount, ushort valueCount, out string error)
    {
        var ids = new HashSet<byte>();
        error = string.Empty;
        foreach (var buttons in new[] { true, false })
        {
            var count = buttons ? buttonCount : valueCount;
            if (count == 0) continue;
            var entries = new HidNativeMethods.ReportCaps[count];
            var status = buttons
                ? HidNativeMethods.HidP_GetButtonCaps(reportType, entries, ref count, preparsed)
                : HidNativeMethods.HidP_GetValueCaps(reportType, entries, ref count, preparsed);
            if (status != HidpStatusSuccess)
            {
                error = $"reportType={reportType}, buttons={buttons}, counts={buttonCount}/{valueCount}, returned={count}, status=0x{status:X8}";
                return null;
            }
            foreach (var entry in entries.Take(count)) ids.Add(entry.ReportId);
        }
        return ids;
    }

    internal sealed class WindowsHidReportTransport : IHidReportTransport
    {
        private readonly Stream _stream;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly SemaphoreSlim _transactionGate = new(1, 1);
        private int _disposed;

        public WindowsHidReportTransport(HidCollectionInfo collection, SafeFileHandle handle)
        {
            Collection = collection;
            _stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
        }

        internal WindowsHidReportTransport(HidCollectionInfo collection, Stream stream)
        {
            Collection = collection;
            _stream = stream;
        }

        public HidCollectionInfo Collection { get; }

        public async ValueTask<IHidReportTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _transactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) != 0)
            {
                _transactionGate.Release();
                throw new ObjectDisposedException(nameof(WindowsHidReportTransport));
            }
            return new Transaction(this);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            // Cancel active overlapped reads before closing. Do not wait for the caller to
            // dispose its transaction: it may be waiting for that very read to finish.
            _lifetime.Cancel();
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        private sealed class Transaction(WindowsHidReportTransport owner) : IHidReportTransaction
        {
            private int _disposed;

            public async ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner._lifetime.Token);
                await owner._stream.WriteAsync(report, linked.Token).ConfigureAwait(false);
            }

            public async ValueTask<int> ReadAsync(Memory<byte> report, CancellationToken cancellationToken = default)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner._lifetime.Token);
                return await owner._stream.ReadAsync(report, linked.Token).ConfigureAwait(false);
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner._transactionGate.Release();
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    private static class HidNativeMethods
    {
        public const int ErrorNoMoreItems = 259;
        public static readonly nint InvalidHandleValue = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct DeviceInterfaceData
        {
            public int Size;
            public Guid InterfaceClassGuid;
            public int Flags;
            public nint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HidpCaps
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        // HIDP_BUTTON_CAPS and HIDP_VALUE_CAPS are both 72 bytes, with ReportID at offset 2.
        [StructLayout(LayoutKind.Explicit, Size = 72)]
        public struct ReportCaps
        {
            [FieldOffset(2)] public byte ReportId;
        }

        [DllImport("hid.dll")]
        public static extern int HidP_GetButtonCaps(int reportType, [Out] ReportCaps[] caps, ref ushort count, nint preparsed);

        [DllImport("hid.dll")]
        public static extern int HidP_GetValueCaps(int reportType, [Out] ReportCaps[] caps, ref ushort count, nint preparsed);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out nint preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_FreePreparsedData(nint preparsedData);

        [DllImport("hid.dll")]
        public static extern int HidP_GetCaps(nint preparsedData, out HidpCaps capabilities);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, nint hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInterfaces(nint deviceInfoSet, nint deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref DeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(nint deviceInfoSet, ref DeviceInterfaceData deviceInterfaceData, nint deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, nint deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);
    }
}

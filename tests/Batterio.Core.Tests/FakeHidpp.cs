using System.Collections.Concurrent;
using System.Text;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

/// <summary>A scripted HID++ 2.0 device.</summary>
internal sealed record FakeHidppDevice(
    string? Name,
    ushort ModelId,
    bool Unified,
    bool Legacy,
    byte Percentage,
    byte UnifiedStatus,
    byte LegacyStatus,
    byte ExternalPower)
{
    public const byte DeviceInfoIndex = 1;
    public const byte NameIndex = 2;
    public const byte UnifiedIndex = 3;
    public const byte LegacyIndex = 4;

    public static FakeHidppDevice Other() =>
        new("MX Anywhere 3S", 0xB037, true, false, 20, 0, 0, 0);

    public static FakeHidppDevice MxMaster3s(
        byte percentage = 50, byte unifiedStatus = 0, byte externalPower = 0, ushort modelId = 0xB034) =>
        new("MX Master 3S", modelId, true, false, percentage, unifiedStatus, 0, externalPower);

    public static FakeHidppDevice MxMechanical(
        byte percentage = 50, byte legacyStatus = 0, bool unified = true, ushort modelId = 0xB366) =>
        new("MX Mechanical", modelId, unified, !unified, percentage, 0, legacyStatus, 0);

    public byte IndexOf(ushort feature) => feature switch
    {
        0x0003 => DeviceInfoIndex,
        0x0005 when Name is not null => NameIndex,
        0x1004 when Unified => UnifiedIndex,
        0x1000 when Legacy => LegacyIndex,
        _ => 0
    };
}

internal sealed record FakeWrite(string Path, byte[] Report);

internal sealed class ListLog : IBatterioLog
{
    private readonly ConcurrentQueue<string> _messages = new();

    public string[] Messages => _messages.ToArray();

    public void Log(BatterioLogLevel level, string message, Exception? exception = null) => _messages.Enqueue(message);
}

/// <summary>
/// A scripted HID provider: Logi Bolt receivers with paired HID++ 2.0 devices, receiver register handling and connection notices,
/// and devices connected over Bluetooth LE. Nothing here comes from real hardware; it encodes the byte layouts documented on
/// <see cref="LogiBoltReceiver"/>.
/// </summary>
internal sealed class FakeHidpp : IHidCollectionProvider
{
    public const string ReceiverLongPath = @"\\?\hid#vid_046d&pid_c548&mi_02&col02#7&1edd123a&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    public const string ReceiverShortPath = @"\\?\hid#vid_046d&pid_c548&mi_02&col01#7&1edd123a&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<byte, FakeHidppDevice>> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HidCollectionInfo> _collections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ReceiverState> _receivers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _shortSiblingOf = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _longOf = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _silent = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failingWrites = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<FakeWrite> _writes = new();
    private readonly ConcurrentQueue<string> _opened = new();
    private readonly ConcurrentQueue<string> _disposed = new();
    private readonly ConcurrentQueue<HidCollectionQuery> _singleEnumerations = new();
    private int _combinedEnumerations;
    private Func<HidCollectionQuery, bool> _failingEnumeration = _ => false;

    public IEnumerable<FakeWrite> Writes => _writes.ToArray();

    public string[] OpenedPaths => _opened.ToArray();

    public string[] DisposedPaths => _disposed.ToArray();

    /// <summary>Calls of the single-pass <c>Enumerate(IReadOnlyCollection&lt;HidCollectionQuery&gt;)</c>.</summary>
    public int CombinedEnumerations => Volatile.Read(ref _combinedEnumerations);

    /// <summary>Calls of the single-query <c>Enumerate(HidCollectionQuery)</c>, including those made by the default combined method.</summary>
    public HidCollectionQuery[] SingleEnumerations => _singleEnumerations.ToArray();

    /// <summary>Enumeration of matching queries fails as when Windows cannot inspect a collection's descriptors.</summary>
    public FakeHidpp FailEnumeration(Func<HidCollectionQuery, bool> predicate)
    {
        _failingEnumeration = predicate;
        return this;
    }

    /// <summary>Writes to the collection fail with an I/O error, as when the device disappears mid-request.</summary>
    public FakeHidpp FailWrites(string path)
    {
        _failingWrites.Add(path);
        return this;
    }

    /// <summary>Writes work again, as after the receiver was plugged back in.</summary>
    public void RestoreWrites(string path) => _failingWrites.Remove(path);

    /// <summary>Pushes a raw short report from the receiver.</summary>
    public void NotifyRaw(string receiverPath, byte[] shortReport) => Deliver(receiverPath, shortReport);

    public void ClearWrites() => _writes.Clear();

    public void ClearOpened() => _opened.Clear();

    /// <summary>A receiver; with <paramref name="withShortCollection"/> it has the short-report sibling collection Windows exposes.</summary>
    public FakeHidpp Receiver(
        string path = ReceiverLongPath,
        bool withShortCollection = true,
        string shortPath = ReceiverShortPath)
    {
        EnsureReceiver(path);
        if (withShortCollection)
        {
            _shortSiblingOf[path] = shortPath;
            _longOf[shortPath] = path;
            _collections[shortPath] = new HidCollectionInfo
            {
                DevicePath = shortPath,
                VendorId = 0x046D,
                ProductId = 0xC548,
                InterfaceNumber = 2,
                UsagePage = 0xFF00,
                Usage = 0x0001,
                InputReportLength = 7,
                OutputReportLength = 7,
                SupportedReportIds = new HashSet<byte> { 0x10 }
            };
        }

        return this;
    }

    /// <summary>
    /// Pairs a device with a receiver slot. <paramref name="online"/> false makes it unreachable (a sleeping device);
    /// <paramref name="announce"/> false leaves it out of the receiver's arrival notices.
    /// </summary>
    public FakeHidpp Add(string path, byte slot, FakeHidppDevice device, bool online = true, bool announce = true)
    {
        var state = EnsureReceiver(path);
        lock (_gate)
        {
            _slots[path][slot] = device;
            state.Announcements[slot] = new Announcement(device.ModelId, online, announce);
        }

        return this;
    }

    public void Remove(string path, byte slot)
    {
        lock (_gate)
        {
            _slots[path].Remove(slot);
            _receivers[path].Announcements.Remove(slot);
        }
    }

    public FakeHidpp SetFlags(string path, byte[] flags)
    {
        EnsureReceiver(path).Flags = flags;
        return this;
    }

    /// <summary>The receiver sends no connection notices when asked to announce its devices (as if the notice path did not work).</summary>
    public FakeHidpp WithoutArrivalNotices(string path = ReceiverLongPath)
    {
        EnsureReceiver(path).AnnounceOnTrigger = false;
        return this;
    }

    /// <summary>The receiver never answers register requests.</summary>
    public FakeHidpp SilentRegisters(string path = ReceiverLongPath)
    {
        EnsureReceiver(path).SilentRegisters = true;
        return this;
    }

    /// <summary>The receiver answers every register request with a HID++ 1.0 error.</summary>
    public FakeHidpp RejectRegisters(string path = ReceiverLongPath)
    {
        EnsureReceiver(path).RejectRegisters = true;
        return this;
    }

    /// <summary>A device connected over Bluetooth LE: its HID++ collection answers on device index 0xFF.</summary>
    public FakeHidpp Bluetooth(string path, ushort productId, FakeHidppDevice? device)
    {
        _slots[path] = device is null ? new Dictionary<byte, FakeHidppDevice>() : new Dictionary<byte, FakeHidppDevice> { [0xFF] = device };
        _silent.Add(path);
        _collections[path] = new HidCollectionInfo
        {
            DevicePath = path,
            VendorId = 0x046D,
            ProductId = productId,
            UsagePage = 0xFF43,
            Usage = 0x0202,
            InputReportLength = 20,
            OutputReportLength = 20,
            SupportedReportIds = new HashSet<byte> { 0x11 }
        };
        return this;
    }

    public void Collection(string path, Func<HidCollectionInfo, HidCollectionInfo> change) =>
        _collections[path] = change(_collections[path]);

    public void RemoveCollection(string path)
    {
        _collections.Remove(path);
        _slots.Remove(path);
        _receivers.Remove(path);
        if (_shortSiblingOf.Remove(path, out var shortPath))
        {
            _collections.Remove(shortPath);
            _longOf.Remove(shortPath);
        }
    }

    /// <summary>Pushes a connection notice (sub-ID 0x41) from the receiver, as when a device wakes up or goes away.</summary>
    public void Notify(string receiverPath, byte slot, ushort wpid, bool online) =>
        Deliver(receiverPath, ConnectionNotice(slot, wpid, online));

    /// <summary>Pushes a notice that the device in <paramref name="slot"/> was un-paired (sub-ID 0x40, address 0x02).</summary>
    public void NotifyUnpaired(string receiverPath, byte slot) =>
        Deliver(receiverPath, new byte[] { 0x10, slot, 0x40, 0x02, 0x00, 0x00, 0x00 });

    public static byte[] ConnectionNotice(byte slot, ushort wpid, bool online) =>
        new byte[] { 0x10, slot, 0x41, 0x10, (byte)(online ? 0x20 : 0x60), (byte)wpid, (byte)(wpid >> 8) };

    private ReceiverState EnsureReceiver(string path)
    {
        lock (_gate)
        {
            if (!_receivers.TryGetValue(path, out var state))
            {
                _receivers[path] = state = new ReceiverState();
                _slots[path] = new Dictionary<byte, FakeHidppDevice>();
                _collections[path] = new HidCollectionInfo
                {
                    DevicePath = path,
                    VendorId = 0x046D,
                    ProductId = 0xC548,
                    InterfaceNumber = 2,
                    UsagePage = 0xFF00,
                    InputReportLength = 20,
                    OutputReportLength = 20,
                    SupportedReportIds = new HashSet<byte> { 0x11 }
                };
            }

            return state;
        }
    }

    public IReadOnlyList<HidCollectionInfo> Enumerate(HidCollectionQuery query)
    {
        _singleEnumerations.Enqueue(query);
        return _failingEnumeration(query)
            ? throw new BatteryAdapterException(BatteryAdapterErrorKind.AccessFailure, "Could not inspect HID descriptors.")
            : _collections.Values.ToArray();
    }

    public IReadOnlyList<HidCollectionInfo> Enumerate(IReadOnlyCollection<HidCollectionQuery> queries)
    {
        Interlocked.Increment(ref _combinedEnumerations);
        var found = new List<HidCollectionInfo>();
        foreach (var query in queries.Distinct())
        {
            found.AddRange(Enumerate(query));
        }

        return found.DistinctBy(collection => collection.DevicePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public ValueTask<IHidReportTransport> OpenAsync(HidCollectionInfo collection, CancellationToken cancellationToken = default)
    {
        _opened.Enqueue(collection.DevicePath);
        var session = new Session(this, collection);
        _sessions[collection.DevicePath] = session;
        return ValueTask.FromResult<IHidReportTransport>(session);
    }

    /// <summary>Delivers a short report on the receiver's short collection if it is open, else a long one on the long collection.</summary>
    private void Deliver(string longPath, byte[] shortReport)
    {
        if (_shortSiblingOf.TryGetValue(longPath, out var shortPath) && _sessions.TryGetValue(shortPath, out var shortSession))
        {
            shortSession.Enqueue(shortReport);
            return;
        }

        var longReport = new byte[20];
        shortReport.CopyTo(longReport, 0);
        longReport[0] = 0x11;
        if (_sessions.TryGetValue(longPath, out var longSession)) longSession.Enqueue(longReport);
    }

    private void HandleRegister(string sessionPath, byte[] request)
    {
        var longPath = _longOf.TryGetValue(sessionPath, out var mapped) ? mapped : sessionPath;
        var state = _receivers[longPath];
        var sub = request[2];
        var address = request[3];
        if (state.SilentRegisters)
        {
            return;
        }

        if (state.RejectRegisters)
        {
            Deliver(longPath, new byte[] { 0x10, 0xFF, 0x8F, sub, address, 0x02, 0x00 });
            return;
        }

        if (sub == 0x81 && address == 0x00)
        {
            Deliver(longPath, new byte[] { 0x10, 0xFF, 0x81, 0x00, state.Flags[0], state.Flags[1], state.Flags[2] });
        }
        else if (sub == 0x80 && address == 0x00)
        {
            state.Flags = new[] { request[4], request[5], request[6] };
            Deliver(longPath, new byte[] { 0x10, 0xFF, 0x80, 0x00, 0x00, 0x00, 0x00 });
        }
        else if (sub == 0x80 && address == 0x02 && request[4] == 0x02)
        {
            Deliver(longPath, new byte[] { 0x10, 0xFF, 0x80, 0x02, 0x00, 0x00, 0x00 });
            if (state.AnnounceOnTrigger)
            {
                foreach (var (slot, announcement) in state.Announcements.OrderBy(pair => pair.Key).ToArray())
                {
                    if (announcement.Announce) Deliver(longPath, ConnectionNotice(slot, announcement.Wpid, announcement.Online));
                }
            }
        }
        else
        {
            Deliver(longPath, new byte[] { 0x10, 0xFF, 0x8F, sub, address, 0x02, 0x00 });
        }
    }

    private sealed record Announcement(ushort Wpid, bool Online, bool Announce);

    private sealed class ReceiverState
    {
        public byte[] Flags = { 0, 0, 0 };
        public bool AnnounceOnTrigger = true;
        public bool RejectRegisters;
        public bool SilentRegisters;
        public Dictionary<byte, Announcement> Announcements { get; } = new();
    }

    private sealed class Session(FakeHidpp owner, HidCollectionInfo collection) : IHidReportTransport, IHidReportTransaction
    {
        private readonly ConcurrentQueue<byte[]> _replies = new();
        private readonly SemaphoreSlim _available = new(0);

        public HidCollectionInfo Collection => collection;

        public ValueTask<IHidReportTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IHidReportTransaction>(this);

        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default)
        {
            if (owner._failingWrites.Contains(collection.DevicePath))
            {
                return ValueTask.FromException(new IOException("The device is not connected."));
            }

            var bytes = report.ToArray();
            owner._writes.Enqueue(new FakeWrite(collection.DevicePath, bytes));
            if (bytes[1] == 0xFF && bytes[2] is 0x80 or 0x81 && owner._receivers.ContainsKey(
                    owner._longOf.TryGetValue(collection.DevicePath, out var longPath) ? longPath : collection.DevicePath))
            {
                owner.HandleRegister(collection.DevicePath, bytes);
                return ValueTask.CompletedTask;
            }

            var reply = owner.Reply(collection.DevicePath, bytes);
            if (reply is null)
            {
                return ValueTask.CompletedTask;
            }

            if (reply[2] == 0x8F && owner._shortSiblingOf.TryGetValue(collection.DevicePath, out var shortPath))
            {
                // Delivered as a short report on the sibling collection, or lost when nobody reads it.
                if (owner._sessions.TryGetValue(shortPath, out var sibling))
                {
                    var shortReply = reply[..7];
                    shortReply[0] = 0x10;
                    sibling.Enqueue(shortReply);
                }

                return ValueTask.CompletedTask;
            }

            Enqueue(reply);
            return ValueTask.CompletedTask;
        }

        public void Enqueue(byte[] reply)
        {
            _replies.Enqueue(reply);
            _available.Release();
        }

        public async ValueTask<int> ReadAsync(Memory<byte> report, CancellationToken cancellationToken = default)
        {
            await _available.WaitAsync(cancellationToken);
            if (!_replies.TryDequeue(out var reply)) throw new InvalidOperationException();
            reply.CopyTo(report);
            return reply.Length;
        }

        public ValueTask DisposeAsync()
        {
            owner._disposed.Enqueue(collection.DevicePath);
            return ValueTask.CompletedTask;
        }
    }

    private byte[]? Reply(string path, byte[] request)
    {
        var response = new byte[20];
        response[0] = 0x11;
        response[1] = request[1];
        response[2] = request[2];
        response[3] = request[3];

        FakeHidppDevice? device;
        lock (_gate)
        {
            device = _slots.TryGetValue(path, out var slots) && slots.TryGetValue(request[1], out var found) ? found : null;
            if (device is not null && _receivers.TryGetValue(path, out var receiver) &&
                receiver.Announcements.TryGetValue(request[1], out var announcement) && !announcement.Online)
            {
                device = null; // a sleeping device behind a receiver
            }
        }

        if (device is null)
        {
            if (_silent.Contains(path)) return null; // A sleeping Bluetooth device simply does not answer.

            // HID++ 1.0 receiver error: unknown device.
            response[2] = 0x8F;
            response[3] = request[2];
            response[4] = request[3];
            response[5] = 0x08;
            return response;
        }

        var feature = request[2];
        var function = request[3] & 0xF0;
        if (feature == 0)
        {
            if (function == 0x10)
            {
                response[4] = 4;
                response[5] = 2;
                response[6] = request[6];
            }
            else
            {
                response[4] = device.IndexOf((ushort)((request[4] << 8) | request[5]));
            }
        }
        else if (feature == FakeHidppDevice.DeviceInfoIndex)
        {
            response[4] = 3;
            response[10] = 0x02; // BLE transport only (Logi Bolt devices talk BLE to the receiver).
            response[11] = (byte)(device.ModelId >> 8);
            response[12] = (byte)device.ModelId;
        }
        else if (feature == FakeHidppDevice.NameIndex)
        {
            var name = Encoding.UTF8.GetBytes(device.Name!);
            if (function == 0)
            {
                response[4] = (byte)name.Length;
            }
            else
            {
                var offset = request[4];
                name.AsSpan(offset, Math.Min(16, name.Length - offset)).CopyTo(response.AsSpan(4));
            }
        }
        else if (feature == FakeHidppDevice.UnifiedIndex)
        {
            if (function == 0)
            {
                response[4] = 15;
                response[5] = 3; // percentage supported
            }
            else
            {
                response[4] = device.Percentage;
                response[5] = 8;
                response[6] = device.UnifiedStatus;
                response[7] = device.ExternalPower;
            }
        }
        else if (feature == FakeHidppDevice.LegacyIndex)
        {
            response[4] = device.Percentage;
            response[5] = 20;
            response[6] = device.LegacyStatus;
        }

        return response;
    }
}

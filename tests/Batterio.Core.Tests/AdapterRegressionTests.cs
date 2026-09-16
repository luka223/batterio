using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Batterio.App.Windows;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

public sealed class AdapterRegressionTests
{
    [Fact]
    public void NativeDetailBufferPreservesTheCompleteDevicePath()
    {
        const string path = @"\\?\hid#vid_046d&pid_c54d&mi_02#receiver";
        var bytes = Encoding.Unicode.GetBytes(path + '\0');
        var buffer = Marshal.AllocHGlobal(bytes.Length + 4);
        try
        {
            Marshal.WriteInt32(buffer, 8);
            Marshal.Copy(bytes, 0, buffer + 4, bytes.Length);
            Assert.Equal(path, WindowsHidCollectionProvider.ReadDevicePath(buffer));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [Fact]
    public void NativeCapsLayoutIncludesLinkCollectionCount()
    {
        var native = typeof(WindowsHidCollectionProvider).GetNestedType("HidNativeMethods", System.Reflection.BindingFlags.NonPublic)!;
        var caps = native.GetNestedType("HidpCaps")!;
        Assert.Equal(64, Marshal.SizeOf(caps));
        Assert.Equal(46, Marshal.OffsetOf(caps, "NumberInputButtonCaps").ToInt32());
        Assert.Equal(48, Marshal.OffsetOf(caps, "NumberInputValueCaps").ToInt32());
        Assert.Equal(54, Marshal.OffsetOf(caps, "NumberOutputValueCaps").ToInt32());
    }

    [Fact]
    public async Task SplitReceiverCollectionsSelectLongReportsForIdentityReplies()
    {
        var shortCollection = Mouse() with
        {
            DevicePath = "short",
            InputReportLength = 7,
            OutputReportLength = 7,
            SupportedReportIds = new HashSet<byte> { 0x10 }
        };
        var longCollection = Mouse() with
        {
            DevicePath = "long",
            SupportedReportIds = new HashSet<byte> { 0x11 }
        };
        var provider = new ScriptProvider(longCollection, r => MouseReply(r))
        {
            Collections = new[] { shortCollection, longCollection }
        };
        await using var adapter = new LogitechGProX2SuperstrikeAdapter(provider);
        Assert.Equal(15, (await adapter.GetBatteryStatus()).Percentage);
        Assert.All(provider.Writes, report => Assert.Equal(0x11, report[0]));
        Assert.Equal("long", provider.Sessions.Single().Collection.DevicePath);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(15, 15)]
    [InlineData(100, 100)]
    public async Task MouseReadsPercentageOnlyWhenCapabilityIsPresent(byte wire, int expected)
    {
        var provider = new ScriptProvider(Mouse(), r => MouseReply(r, wire));
        await using var adapter = new LogitechGProX2SuperstrikeAdapter(provider);
        var reading = await adapter.GetBatteryStatus();
        Assert.Equal(expected, reading.Percentage);
        Assert.Equal(1, provider.Closed);
        Assert.Equal(1, provider.TransactionsClosed);
    }

    [Fact]
    public async Task MouseWithoutPercentageCapabilityReturnsOnlyStatus()
    {
        var provider = new ScriptProvider(Mouse(), r => MouseReply(r, 255, false));
        await using var adapter = new LogitechGProX2SuperstrikeAdapter(provider);
        var reading = await adapter.GetBatteryStatus();
        Assert.Null(reading.Percentage);
        Assert.Equal(BatteryChargeState.Charging, reading.ChargeState);
        Assert.Equal(BatteryChargeState.Offline,
            LogitechBatteryDecoding.DecodeSuperstrike(new byte[] { 0, 0, 255 }, false).ChargeState);
        Assert.Throws<BatteryAdapterException>(() => LogitechBatteryDecoding.SupportsPercentage(new byte[] { 3 }));
    }

    [Theory]
    [InlineData(0x8F, 4, BatteryAdapterErrorKind.DeviceOffline)]
    [InlineData(0x8F, 8, BatteryAdapterErrorKind.DeviceOffline)]
    [InlineData(0x8F, 7, BatteryAdapterErrorKind.DeviceBusy)]
    [InlineData(0xFF, 8, BatteryAdapterErrorKind.DeviceBusy)]
    [InlineData(0xFF, 9, BatteryAdapterErrorKind.UnsupportedCapability)]
    [InlineData(0xFF, 6, BatteryAdapterErrorKind.UnsupportedCapability)]
    [InlineData(0xFF, 7, BatteryAdapterErrorKind.UnsupportedCapability)]
    [InlineData(0xFF, 4, BatteryAdapterErrorKind.ProtocolError)]
    public async Task HidppErrorsUseTheEchoedRequestAndActualErrorByte(byte envelope, byte code, BatteryAdapterErrorKind expected)
    {
        var provider = new ScriptProvider(Mouse(), _ => new byte[] { 0x10, 1, envelope, 9, 0x11, code, 0 });
        await using var transport = await provider.OpenAsync(Mouse());
        await using var transaction = await transport.BeginTransactionAsync();
        var client = new HidppClient(transaction, Mouse(), new NullBatterioLog());
        var error = await Assert.ThrowsAsync<BatteryAdapterException>(() => client.SendAsync(1, 9, 0x10, ReadOnlyMemory<byte>.Empty, default));
        Assert.Equal(expected, error.Kind);
    }

    [Theory]
    [InlineData(0x8F)]
    [InlineData(0xFF)]
    public async Task UnrelatedErrorsAreIgnored(byte envelope)
    {
        var provider = new ScriptProvider(Mouse(), _ => null);
        await using var transport = await provider.OpenAsync(Mouse());
        await using var transaction = await transport.BeginTransactionAsync();
        var session = provider.Sessions.Single();
        session.Replies.Enqueue(new byte[] { 0x10, 1, envelope, 8, 0x11, 4, 0 });
        session.Replies.Enqueue(new byte[] { 0x10, 1, 9, 0x11, 15, 0, 2 });
        var client = new HidppClient(transaction, Mouse(), new NullBatterioLog());
        var response = await client.SendAsync(1, 9, 0x10, ReadOnlyMemory<byte>.Empty, default);
        Assert.Equal(15, response.Parameters[0]);
    }

    [Fact]
    public async Task CorrelatedTruncatedErrorIsMalformed()
    {
        var provider = new ScriptProvider(Mouse(), _ => new byte[] { 0x10, 1, 0xFF, 9, 0x11 });
        await using var transport = await provider.OpenAsync(Mouse());
        await using var transaction = await transport.BeginTransactionAsync();
        var client = new HidppClient(transaction, Mouse(), new NullBatterioLog());
        var error = await Assert.ThrowsAsync<BatteryAdapterException>(() => client.SendAsync(1, 9, 0x10, ReadOnlyMemory<byte>.Empty, default));
        Assert.Equal(BatteryAdapterErrorKind.MalformedResponse, error.Kind);
    }

    [Fact]
    public async Task SilentFirstSlotDoesNotPreventDiscoveryOfSecondSlot()
    {
        var provider = new ScriptProvider(Mouse(), r => r[1] == 1 ? null : MouseReply(r));
        await using var adapter = new LogitechGProX2SuperstrikeAdapter(provider);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var reading = await adapter.GetBatteryStatus(deadline.Token);
        Assert.Equal(15, reading.Percentage);
        Assert.Contains(provider.Writes, r => r[1] == 2);
    }

    [Fact]
    public async Task OverallDeadlineCancelsProbingAndClosesSession()
    {
        var provider = new ScriptProvider(Mouse(), _ => null);
        await using var adapter = new LogitechGProX2SuperstrikeAdapter(provider);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetBatteryStatus(deadline.Token));
        Assert.Single(provider.Writes);
        Assert.Equal(1, provider.Closed);
        Assert.Equal(1, provider.TransactionsClosed);
    }

    [Fact]
    public async Task HealthyHeadsetDoesNotWaitForSilentMouse()
    {
        var mouseProvider = new ScriptProvider(Mouse(), _ => null);
        var headsetProvider = new ScriptProvider(Headset(), LegacyReply);
        await using var mouse = new LogitechGProX2SuperstrikeAdapter(mouseProvider);
        await using var headset = new LogitechGProX2LightspeedAdapter(headsetProvider);
        using var deadline = new CancellationTokenSource();
        var blocked = mouse.GetBatteryStatus(deadline.Token);
        await mouseProvider.Opened.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            using var headsetDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            Assert.Equal(15, (await headset.GetBatteryStatus(headsetDeadline.Token)).Percentage);
        }
        finally
        {
            deadline.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        }
    }

    [Fact]
    public async Task SamePathInstancesSerializeAndRecoverAfterCancellation()
    {
        var provider = new ScriptProvider(Mouse(), _ => null);
        await using var first = new LogitechGProX2SuperstrikeAdapter(provider);
        await using var second = new LogitechGProX2SuperstrikeAdapter(provider, devicePath: "MOUSE");
        using var firstCancel = new CancellationTokenSource();
        using var waitingCancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var active = first.GetBatteryStatus(firstCancel.Token);
        await provider.Opened.Task;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.GetBatteryStatus(waitingCancel.Token));
        Assert.Single(provider.Sessions);
        firstCancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        provider.Respond = r => MouseReply(r);
        Assert.Equal(15, (await second.GetBatteryStatus()).Percentage);
        Assert.Equal(2, provider.Closed);
        Assert.Equal(1, provider.MaximumOpen);
    }

    [Fact]
    public async Task ReconnectionRediscoversChangedFeatureIndices()
    {
        var provider = new ScriptProvider(Mouse(), r => MouseReply(r));
        await using var adapter = new LogitechGProX2SuperstrikeAdapter(provider);
        Assert.Equal(15, (await adapter.GetBatteryStatus()).Percentage);
        provider.Respond = r => MouseReply(r, 80, true, 12, 25);
        Assert.Equal(80, (await adapter.GetBatteryStatus()).Percentage);
        Assert.Contains(provider.Writes, r => r[2] == 25 && r[3] == 0x11);
        Assert.Equal(2, provider.Closed);
    }

    [Fact]
    public async Task ReloadWaitsForOldReceiverSessionToClose()
    {
        var provider = new ScriptProvider(Mouse(), _ => null);
        await using var coordinator = new PollingCoordinator(new AdapterRegistry(new[] { new LogitechGProX2SuperstrikeAdapterFactory(provider) }));
        var device = new DeviceConfiguration
        {
            Id = "mouse",
            Name = "Mouse",
            AdapterId = "logitech-g-pro-x2-superstrike"
        };
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StateChanged += (_, args) =>
        {
            if (args.State.Percentage == 80) ready.TrySetResult();
        };
        await coordinator.StartDevicesAsync(new[] { device });
        await provider.Opened.Task.WaitAsync(TimeSpan.FromSeconds(2));
        provider.Respond = r => MouseReply(r, 80);
        await coordinator.StartDevicesAsync(new[] { device });
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(80, coordinator.States.Single().Percentage);
        Assert.Equal(1, provider.MaximumOpen);
        Assert.Equal(2, provider.Closed);
    }

    [Fact]
    public async Task DisposingAdapterCancelsItsPendingRead()
    {
        var provider = new ScriptProvider(Mouse(), _ => null);
        var adapter = new LogitechGProX2SuperstrikeAdapter(provider);
        var pending = adapter.GetBatteryStatus();
        await provider.Opened.Task;
        await adapter.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, provider.Closed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => adapter.GetBatteryStatus());
    }

    [Fact]
    public async Task ExplicitMissingPathNeverOpensAnotherReceiver()
    {
        var provider = new ScriptProvider(Mouse(), r => MouseReply(r));
        await using var adapter = new LogitechGProX2SuperstrikeAdapter(provider, devicePath: "missing");
        var error = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());
        Assert.Equal(BatteryAdapterErrorKind.ReceiverMissing, error.Kind);
        Assert.Empty(provider.Sessions);
        Assert.False(new HidCollectionQuery { VendorId = 0x046D, ProductId = 0xC54D, RequiredReportId = 0x10 }
            .Matches(Mouse() with { SupportedReportIds = null }));
    }

    [Fact]
    public async Task NativeOverlappedReadCancelsAndTransportDisposalClosesHandle()
    {
        var name = "batterio-test-" + Guid.NewGuid();
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accept = server.WaitForConnectionAsync();
        await client.ConnectAsync();
        await accept;
        var handle = client.SafePipeHandle;
        await using var transport = new WindowsHidCollectionProvider.WindowsHidReportTransport(Mouse(), client);
        await using var transaction = await transport.BeginTransactionAsync();
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transaction.ReadAsync(new byte[20], cancellation.Token).AsTask());
        var pending = transaction.ReadAsync(new byte[20]).AsTask();
        await transport.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(handle.IsClosed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.BeginTransactionAsync().AsTask());
    }

    private static HidCollectionInfo Mouse() => new()
    {
        DevicePath = "mouse",
        VendorId = 0x046D,
        ProductId = 0xC54D,
        InterfaceNumber = 2,
        UsagePage = 0xFF00,
        InputReportLength = 20,
        OutputReportLength = 20,
        SupportedReportIds = new HashSet<byte> { 0x10, 0x11 }
    };

    private static HidCollectionInfo Headset() => new()
    {
        DevicePath = "headset",
        VendorId = 0x046D,
        ProductId = 0x0AF7,
        InterfaceNumber = 3,
        UsagePage = 0xFFA0,
        Usage = 1,
        InputReportLength = 64,
        OutputReportLength = 64,
        SupportedReportIds = new HashSet<byte> { 0x51 }
    };

    private static byte[] MouseReply(byte[] request, byte percentage = 15, bool percentageSupported = true, byte identity = 3, byte battery = 9)
    {
        var response = new byte[20];
        request.AsSpan(0, 4).CopyTo(response);
        if (request[2] == 0 && request[3] == 0x11)
        {
            response[4] = 2;
            response[6] = request[6];
        }
        else if (request[2] == 0 && request[3] == 1)
        {
            Assert.True((request[4] == 0 && request[5] == 3) || (request[4] == 0x10 && request[5] == 4));
            response[4] = request[4] == 0 ? identity : battery;
        }
        else if (request[2] == identity && request[3] == 1)
        {
            response[0] = 0x11;
            response[10] = 4;
            response[11] = 0x40;
            response[12] = 0xBD;
        }
        else if (request[2] == battery && request[3] == 1)
        {
            response[4] = 15;
            response[5] = percentageSupported ? (byte)3 : (byte)1;
        }
        else
        {
            Assert.Equal(battery, request[2]);
            Assert.Equal(0x11, request[3]);
            response[4] = percentage;
            response[6] = 2;
        }
        return response;
    }

    private static byte[] LegacyReply(byte[] request)
    {
        var response = new byte[64];
        response[0] = 0x51;
        if (request[1] == 8)
        {
            response[1] = 11;
            response[8] = 4;
            response[10] = 15;
            response[12] = 2;
        }
        else
        {
            response[1] = 5;
            response[3] = 0xFF;
            response[4] = request[3];
            response[5] = request[4];
            response[6] = 9;
        }
        return response;
    }

    private sealed class ScriptProvider(HidCollectionInfo collection, Func<byte[], byte[]?> respond) : IHidCollectionProvider
    {
        public Func<byte[], byte[]?> Respond { get; set; } = respond;
        public IReadOnlyList<HidCollectionInfo> Collections { get; set; } = new[] { collection };
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<Session> Sessions { get; } = new();
        public ConcurrentQueue<byte[]> Writes { get; } = new();
        public int Closed;
        public int TransactionsClosed;
        public int MaximumOpen;
        private int _open;
        public IReadOnlyList<HidCollectionInfo> Enumerate(HidCollectionQuery query) => Collections;
        public ValueTask<IHidReportTransport> OpenAsync(HidCollectionInfo info, CancellationToken cancellationToken = default)
        {
            MaximumOpen = Math.Max(MaximumOpen, Interlocked.Increment(ref _open));
            var session = new Session(this, info);
            Sessions.Enqueue(session);
            Opened.TrySetResult();
            return ValueTask.FromResult<IHidReportTransport>(session);
        }

        public sealed class Session(ScriptProvider owner, HidCollectionInfo collection) : IHidReportTransport, IHidReportTransaction
        {
            public ConcurrentQueue<byte[]> Replies { get; } = new();
            public HidCollectionInfo Collection => collection;
            public ValueTask<IHidReportTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IHidReportTransaction>(this);
            public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = report.ToArray();
                owner.Writes.Enqueue(bytes);
                var response = owner.Respond(bytes);
                if (response is not null) Replies.Enqueue(response);
                return ValueTask.CompletedTask;
            }
            public async ValueTask<int> ReadAsync(Memory<byte> report, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Replies.TryDequeue(out var response))
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    throw new InvalidOperationException();
                }
                response.CopyTo(report);
                return response.Length;
            }
            private int _disposals;
            public ValueTask DisposeAsync()
            {
                if (++_disposals == 1) Interlocked.Increment(ref owner.TransactionsClosed);
                else
                {
                    Interlocked.Increment(ref owner.Closed);
                    Interlocked.Decrement(ref owner._open);
                }
                return ValueTask.CompletedTask;
            }
        }
    }
}

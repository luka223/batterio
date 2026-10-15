using System.Collections.Concurrent;
using System.Text.Json;
using Batterio.Core;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

public sealed class KeychronQ1HeAdapterTests
{
    private const string Path1 = @"\\?\hid#vid_3434&pid_d030&mi_03&col01#a&1";
    private const string Path2 = @"\\?\hid#vid_3434&pid_d030&mi_03&col01#a&2";

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task UnrelatedFramesBeforeAReportAreSkippedAndNothingIsWritten()
    {
        var provider = new LinkProvider(Collection(Path1));
        provider.Frames.Add(new byte[33]);
        provider.Frames.Add(KeychronBatteryReportDecodingTests.Frame(model: 1));
        provider.Frames.Add(KeychronBatteryReportDecodingTests.Frame(72, charge: 1));
        await using var adapter = new KeychronQ1HeAdapter(provider);

        var reading = await adapter.GetBatteryStatus();

        Assert.Equal(new BatteryReading(72, BatteryChargeState.Charging), reading);
        Assert.Empty(provider.Writes);
    }

    [Fact]
    public async Task SilenceIsReportedAsDeviceOffline()
    {
        await using var adapter = new KeychronQ1HeAdapter(new LinkProvider(Collection(Path1)), listenTimeout: ShortTimeout);

        var error = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());

        Assert.Equal(BatteryAdapterErrorKind.DeviceOffline, error.Kind);
    }

    [Fact]
    public async Task AFloodOfUnrelatedFramesIsAProtocolError()
    {
        var provider = new LinkProvider(Collection(Path1));
        for (var index = 0; index < KeychronQ1HeAdapter.MaximumFramesPerQuery + 5; index++)
        {
            provider.Frames.Add(new byte[33]);
        }

        await using var adapter = new KeychronQ1HeAdapter(provider);

        var error = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());

        Assert.Equal(BatteryAdapterErrorKind.ProtocolError, error.Kind);
    }

    [Fact]
    public async Task MissingDongleIsReportedAsReceiverMissing()
    {
        await using var adapter = new KeychronQ1HeAdapter(new LinkProvider());

        var error = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());

        Assert.Equal(BatteryAdapterErrorKind.ReceiverMissing, error.Kind);
    }

    [Fact]
    public async Task TwoDonglesWithoutADevicePathAreAmbiguous()
    {
        await using var adapter = new KeychronQ1HeAdapter(new LinkProvider(Collection(Path1), Collection(Path2)));

        var error = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());

        Assert.Equal(BatteryAdapterErrorKind.ReceiverAmbiguous, error.Kind);
    }

    [Fact]
    public async Task TheDevicePathSelectsTheMatchingDongle()
    {
        var provider = new LinkProvider(Collection(Path1), Collection(Path2))
        {
            Frames = { KeychronBatteryReportDecodingTests.Frame(33) }
        };
        await using var adapter = new KeychronQ1HeAdapter(provider, devicePath: Path2.ToUpperInvariant());

        var reading = await adapter.GetBatteryStatus();

        Assert.Equal(33, reading.Percentage);
        Assert.Equal(Path2, Assert.Single(provider.Opened));
    }

    [Fact]
    public async Task CancellationIsHonoredInsteadOfBecomingOffline()
    {
        var provider = new LinkProvider(Collection(Path1));
        await using var adapter = new KeychronQ1HeAdapter(provider, listenTimeout: TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetBatteryStatus(cancellation.Token));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetBatteryStatus(cancelled.Token));
    }

    [Fact]
    public async Task UseAfterDisposeThrows()
    {
        var adapter = new KeychronQ1HeAdapter(new LinkProvider(Collection(Path1)));
        await adapter.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => adapter.GetBatteryStatus());
    }

    [Fact]
    public void ListeningFitsInsideTheDefaultDeviceTimeout()
    {
        Assert.True(KeychronQ1HeAdapter.DefaultListenTimeout < TimeSpan.FromSeconds(new DeviceConfiguration().TimeoutSeconds));
    }

    [Fact]
    public void FactoryExposesTheAdapterIdAndRejectsAnEmptyDevicePath()
    {
        var factory = new KeychronQ1HeAdapterFactory();

        Assert.Equal("keychron-q1-he", factory.AdapterId);
        factory.ValidateSettings(default);
        factory.ValidateSettings(JsonDocument.Parse("{}").RootElement);
        factory.ValidateSettings(JsonDocument.Parse("""{"devicePath":"abc"}""").RootElement);
        Assert.Throws<AdapterConfigurationException>(() => factory.ValidateSettings(JsonDocument.Parse("""{"devicePath":""}""").RootElement));
    }

    private static HidCollectionInfo Collection(string path) => new()
    {
        DevicePath = path,
        VendorId = 0x3434,
        ProductId = 0xD030,
        InterfaceNumber = 3,
        UsagePage = 0xFF60,
        Usage = 0x0061,
        InputReportLength = 33,
        OutputReportLength = 33,
        FeatureReportLength = 0,
        SupportedReportIds = new HashSet<byte> { 0x00 }
    };

    private sealed class LinkProvider(params HidCollectionInfo[] collections) : IHidCollectionProvider
    {
        public List<byte[]> Frames { get; } = new();

        public ConcurrentQueue<byte[]> Writes { get; } = new();

        public ConcurrentQueue<string> Opened { get; } = new();

        public IReadOnlyList<HidCollectionInfo> Enumerate(HidCollectionQuery query) => collections;

        public ValueTask<IHidReportTransport> OpenAsync(HidCollectionInfo collection, CancellationToken cancellationToken = default)
        {
            Opened.Enqueue(collection.DevicePath);
            return ValueTask.FromResult<IHidReportTransport>(new Transport(this, collection));
        }

        private sealed class Transport(LinkProvider owner, HidCollectionInfo collection) : IHidReportTransport, IHidReportTransaction
        {
            private readonly Queue<byte[]> _frames = new(owner.Frames);

            public HidCollectionInfo Collection => collection;

            public ValueTask<IHidReportTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
                ValueTask.FromResult<IHidReportTransaction>(this);

            public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default)
            {
                owner.Writes.Enqueue(report.ToArray());
                return ValueTask.CompletedTask;
            }

            public async ValueTask<int> ReadAsync(Memory<byte> report, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_frames.TryDequeue(out var frame))
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    throw new InvalidOperationException();
                }

                frame.CopyTo(report);
                return frame.Length;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

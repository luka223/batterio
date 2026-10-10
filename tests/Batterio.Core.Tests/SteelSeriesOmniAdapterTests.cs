using System.Collections.Concurrent;
using System.Text.Json;
using Batterio.Core;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

public sealed class SteelSeriesOmniAdapterTests
{
    private const string Path1 = @"\\?\hid#vid_1038&pid_2290&mi_03&col01#a&1";
    private const string Path2 = @"\\?\hid#vid_1038&pid_2290&mi_03&col01#a&2";

    [Fact]
    public async Task TheStatusRequestIsWrittenAndTheReplyIsDecoded()
    {
        var provider = new OmniProvider(Collection(Path1)) { Frames = { SteelSeriesOmniDecodingTests.Frame(97, 81) } };
        await using var adapter = new SteelSeriesArctisNovaProOmniAdapter(provider);

        var reading = await adapter.GetBatteryStatus();

        Assert.Equal(97, reading.Percentage);
        Assert.Equal(new BatteryReading(81, BatteryChargeState.Charging), reading.Reserve);
        var write = Assert.Single(provider.Writes);
        Assert.Equal(SteelSeriesNovaProOmniStatusDecoding.CreateStatusRequest(64), write);
    }

    [Fact]
    public async Task UnsolicitedEventFramesBeforeTheReplyAreSkipped()
    {
        var provider = new OmniProvider(Collection(Path1));
        provider.Frames.Add(Event(0xB7));
        provider.Frames.Add(Event(0xB5));
        provider.Frames.Add(new byte[64]);
        provider.Frames.Add(SteelSeriesOmniDecodingTests.Frame(40, 70));
        await using var adapter = new SteelSeriesArctisNovaProOmniAdapter(provider);

        var reading = await adapter.GetBatteryStatus();

        Assert.Equal(40, reading.Percentage);
        Assert.Equal(70, reading.Reserve?.Percentage);
    }

    [Fact]
    public async Task EventFloodWithoutAReplyFailsAsAProtocolError()
    {
        var provider = new OmniProvider(Collection(Path1));
        for (var index = 0; index < SteelSeriesArctisNovaProOmniAdapter.MaximumFramesPerQuery + 5; index++)
        {
            provider.Frames.Add(Event(0xB7));
        }

        await using var adapter = new SteelSeriesArctisNovaProOmniAdapter(provider);

        var error = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());

        Assert.Equal(BatteryAdapterErrorKind.ProtocolError, error.Kind);
    }

    [Fact]
    public async Task MissingHubIsReportedAsReceiverMissing()
    {
        await using var adapter = new SteelSeriesArctisNovaProOmniAdapter(new OmniProvider());

        var error = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());

        Assert.Equal(BatteryAdapterErrorKind.ReceiverMissing, error.Kind);
        Assert.Contains("USB-1", error.Message);
    }

    [Fact]
    public async Task TwoHubsWithoutADevicePathAreAmbiguous()
    {
        await using var adapter = new SteelSeriesArctisNovaProOmniAdapter(new OmniProvider(Collection(Path1), Collection(Path2)));

        var error = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());

        Assert.Equal(BatteryAdapterErrorKind.ReceiverAmbiguous, error.Kind);
    }

    [Fact]
    public async Task TheDevicePathSelectsTheMatchingHub()
    {
        var provider = new OmniProvider(Collection(Path1), Collection(Path2)) { Frames = { SteelSeriesOmniDecodingTests.Frame(33, 44) } };
        await using var adapter = new SteelSeriesArctisNovaProOmniAdapter(provider, devicePath: Path2.ToUpperInvariant());

        var reading = await adapter.GetBatteryStatus();

        Assert.Equal(33, reading.Percentage);
        Assert.Equal(Path2, Assert.Single(provider.Opened));
    }

    [Fact]
    public async Task AnExplicitDevicePathThatIsGoneIsNotReplacedByAnotherHub()
    {
        var provider = new OmniProvider(Collection(Path1));
        await using var adapter = new SteelSeriesArctisNovaProOmniAdapter(provider, devicePath: Path2);

        var error = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());

        Assert.Equal(BatteryAdapterErrorKind.ReceiverMissing, error.Kind);
        Assert.Empty(provider.Opened);
    }

    [Fact]
    public async Task NoReplyTimesOutAsBusy()
    {
        var provider = new OmniProvider(Collection(Path1));
        await using var adapter = new SteelSeriesArctisNovaProOmniAdapter(
            provider, replyTimeout: TimeSpan.FromMilliseconds(50), retryDelay: TimeSpan.FromMilliseconds(10));

        var error = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());

        Assert.Equal(BatteryAdapterErrorKind.DeviceBusy, error.Kind);
        Assert.Equal(2, provider.Opened.Count);
    }

    [Fact]
    public async Task ASilentFirstOpenIsRetriedAfterTheSettleDelay()
    {
        var provider = new OmniProvider(Collection(Path1));
        provider.LaterOpens.Enqueue([SteelSeriesOmniDecodingTests.Frame(60, 30)]);
        await using var adapter = new SteelSeriesArctisNovaProOmniAdapter(
            provider, replyTimeout: TimeSpan.FromMilliseconds(50), retryDelay: TimeSpan.FromMilliseconds(10));

        var reading = await adapter.GetBatteryStatus();

        Assert.Equal(60, reading.Percentage);
        Assert.Equal(30, reading.Reserve?.Percentage);
        Assert.Equal(2, provider.Opened.Count);
    }

    [Fact]
    public async Task CancellationDuringTheRetryDelayIsNotReportedAsBusy()
    {
        var provider = new OmniProvider(Collection(Path1));
        await using var adapter = new SteelSeriesArctisNovaProOmniAdapter(
            provider, replyTimeout: TimeSpan.FromMilliseconds(50), retryDelay: TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetBatteryStatus(cancellation.Token));

        Assert.Single(provider.Opened);
    }

    [Fact]
    public async Task AProtocolErrorIsNotRetried()
    {
        var provider = new OmniProvider(Collection(Path1));
        for (var index = 0; index < SteelSeriesArctisNovaProOmniAdapter.MaximumFramesPerQuery + 5; index++)
        {
            provider.Frames.Add(Event(0xB7));
        }

        await using var adapter = new SteelSeriesArctisNovaProOmniAdapter(provider, retryDelay: TimeSpan.FromMilliseconds(10));

        var error = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());

        Assert.Equal(BatteryAdapterErrorKind.ProtocolError, error.Kind);
        Assert.Single(provider.Opened);
    }

    [Fact]
    public void BothAttemptsAndTheSettleDelayFitInsideTheDefaultDeviceTimeout()
    {
        var worstCase = 2 * SteelSeriesArctisNovaProOmniAdapter.DefaultReplyTimeout + SteelSeriesArctisNovaProOmniAdapter.DefaultRetryDelay;

        Assert.True(worstCase < TimeSpan.FromSeconds(new DeviceConfiguration().TimeoutSeconds));
    }

    [Fact]
    public async Task CallerCancellationIsHonoredInsteadOfBecomingATimeout()
    {
        var provider = new OmniProvider(Collection(Path1));
        await using var adapter = new SteelSeriesArctisNovaProOmniAdapter(provider, replyTimeout: TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetBatteryStatus(cancellation.Token));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetBatteryStatus(cancelled.Token));
    }

    [Fact]
    public async Task UseAfterDisposeThrows()
    {
        var adapter = new SteelSeriesArctisNovaProOmniAdapter(new OmniProvider(Collection(Path1)));
        await adapter.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => adapter.GetBatteryStatus());
    }

    [Fact]
    public void FactoryExposesTheAdapterIdAndRejectsAnEmptyDevicePath()
    {
        var factory = new SteelSeriesArctisNovaProOmniAdapterFactory();

        Assert.Equal("steelseries-arctis-nova-pro-omni", factory.AdapterId);
        factory.ValidateSettings(default);
        factory.ValidateSettings(JsonDocument.Parse("{}").RootElement);
        factory.ValidateSettings(JsonDocument.Parse("""{"devicePath":"abc"}""").RootElement);
        Assert.Throws<AdapterConfigurationException>(() => factory.ValidateSettings(JsonDocument.Parse("""{"devicePath":""}""").RootElement));
        Assert.Throws<AdapterConfigurationException>(() => factory.ValidateSettings(JsonDocument.Parse("""{"devicePath":5}""").RootElement));
        Assert.Throws<AdapterConfigurationException>(() => factory.ValidateSettings(JsonDocument.Parse("[]").RootElement));
    }

    [Fact]
    public async Task FactoryCreatesIndependentAdaptersThroughTheRegistry()
    {
        var registry = new AdapterRegistry([new SteelSeriesArctisNovaProOmniAdapterFactory()]);
        var device = BuiltInDevices.CatalogById["steelseries-arctis-nova-pro-omni"].CreateConfiguration();

        await using var first = registry.CreateAdapter(device);
        await using var second = registry.CreateAdapter(device);

        Assert.NotSame(first, second);
    }

    private static byte[] Event(byte opcode)
    {
        var frame = new byte[64];
        frame[0] = SteelSeriesNovaProOmniStatusDecoding.EventPrefix;
        frame[1] = opcode;
        return frame;
    }

    private static HidCollectionInfo Collection(string path) => new()
    {
        DevicePath = path,
        VendorId = 0x1038,
        ProductId = 0x2290,
        InterfaceNumber = 3,
        UsagePage = 0xFFC0,
        Usage = 0x0001,
        InputReportLength = 64,
        OutputReportLength = 64,
        FeatureReportLength = 1036,
        SupportedReportIds = new HashSet<byte> { 0x01 }
    };

    private sealed class OmniProvider(params HidCollectionInfo[] collections) : IHidCollectionProvider
    {
        private bool _firstOpenDone;

        /// <summary>Frames handed to the first transport open.</summary>
        public List<byte[]> Frames { get; } = new();

        /// <summary>Frames handed to each later transport open, in order; opens beyond the queue get no frames.</summary>
        public Queue<List<byte[]>> LaterOpens { get; } = new();

        public ConcurrentQueue<byte[]> Writes { get; } = new();

        public ConcurrentQueue<string> Opened { get; } = new();

        public IReadOnlyList<HidCollectionInfo> Enumerate(HidCollectionQuery query) => collections;

        public ValueTask<IHidReportTransport> OpenAsync(HidCollectionInfo collection, CancellationToken cancellationToken = default)
        {
            Opened.Enqueue(collection.DevicePath);
            List<byte[]> frames;
            if (!_firstOpenDone)
            {
                _firstOpenDone = true;
                frames = Frames;
            }
            else
            {
                frames = LaterOpens.TryDequeue(out var next) ? next : new List<byte[]>();
            }

            return ValueTask.FromResult<IHidReportTransport>(new Transport(this, collection, frames));
        }

        private sealed class Transport(OmniProvider owner, HidCollectionInfo collection, List<byte[]> frames) : IHidReportTransport, IHidReportTransaction
        {
            private readonly Queue<byte[]> _frames = new(frames);

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

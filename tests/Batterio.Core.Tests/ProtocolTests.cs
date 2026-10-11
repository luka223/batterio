using Batterio.Core;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

public sealed partial class ProtocolTests
{
    [Fact]
    public void BatteryReadingRejectsInvalidOrUnavailableValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BatteryReading(-1, BatteryChargeState.NotCharging));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BatteryReading(101, BatteryChargeState.Charging));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BatteryReading(50, (BatteryChargeState)99));
        Assert.Throws<ArgumentException>(() => new BatteryReading(50, BatteryChargeState.Offline));

        var statusOnly = new BatteryReading(null, BatteryChargeState.Charging);
        Assert.Null(statusOnly.Percentage);
        Assert.Equal(BatteryChargeState.Charging, statusOnly.ChargeState);
        Assert.Equal(BatteryChargeState.Offline, default(BatteryChargeState));
        Assert.Equal(BatteryChargeState.Offline, BatteryReading.Offline.ChargeState);
        Assert.Null(BatteryReading.Offline.Percentage);
    }

    [Theory]
    [InlineData(0x00, BatteryChargeState.NotCharging)]
    [InlineData(0x01, BatteryChargeState.Charging)]
    [InlineData(0x02, BatteryChargeState.Charging)]
    [InlineData(0x03, BatteryChargeState.NotCharging)]
    [InlineData(0x04, BatteryChargeState.NotCharging)]
    [InlineData(0x05, BatteryChargeState.Offline)]
    [InlineData(0x06, BatteryChargeState.Offline)]
    [InlineData(0x7F, BatteryChargeState.NotCharging)]
    public void SuperstrikeStatusesAreMappedExplicitly(byte status, BatteryChargeState expected)
    {
        Assert.Equal(expected, LogitechBatteryDecoding.DecodeSuperstrikeStatus(status));
    }

    [Theory]
    [InlineData(0x03, 0x01, BatteryChargeState.Charging)]
    [InlineData(0x03, 0x00, BatteryChargeState.NotCharging)]
    [InlineData(0x03, 0x02, BatteryChargeState.NotCharging)]
    [InlineData(0x04, 0x01, BatteryChargeState.NotCharging)]
    [InlineData(0x01, 0x01, BatteryChargeState.Charging)]
    public void SuperstrikeShowsChargingWhenFullAndExternalPowerIsConnected(
        byte status,
        byte externalPowerStatus,
        BatteryChargeState expected)
    {
        var reading = LogitechBatteryDecoding.DecodeSuperstrike(
            new byte[] { 100, 0x08, status, externalPowerStatus });
        var presentation = BatteryPresentationRules.Create(reading);

        Assert.Equal(expected, reading.ChargeState);
        Assert.Equal(expected == BatteryChargeState.Charging, presentation.IsCharging);
        Assert.Equal("100%", presentation.PercentageText);
    }

    [Theory]
    [InlineData(0x03, false)]
    [InlineData(0x04, true)]
    [InlineData(0x05, true)]
    [InlineData(0x06, true)]
    public void SuperstrikeErrorStatusesAreIdentifiedForDiagnostics(byte status, bool expected)
    {
        Assert.Equal(expected, LogitechBatteryDecoding.IsSuperstrikeErrorStatus(status));
    }

    [Theory]
    [InlineData(0x00, BatteryChargeState.NotCharging)]
    [InlineData(0x01, BatteryChargeState.Charging)]
    [InlineData(0x02, BatteryChargeState.Charging)]
    [InlineData(0x03, BatteryChargeState.NotCharging)]
    [InlineData(0x04, BatteryChargeState.NotCharging)]
    public void CenturionStatusesAreMappedExplicitly(byte status, BatteryChargeState expected)
    {
        Assert.Equal(expected, LogitechBatteryDecoding.DecodeCenturionStatus(status));
    }

    [Fact]
    public void BatteryDecodersDoNotFabricatePercentagesOrAcceptMalformedValues()
    {
        Assert.Equal(BatteryChargeState.NotCharging,
            LogitechBatteryDecoding.DecodeSuperstrike(new byte[] { 15 }).ChargeState);
        Assert.Equal(BatteryChargeState.NotCharging,
            LogitechBatteryDecoding.DecodeCenturion(new byte[] { 15 }).ChargeState);
        Assert.Equal(BatteryChargeState.Offline,
            LogitechBatteryDecoding.DecodeSuperstrike(new byte[] { 15 }, supportsPercentage: false).ChargeState);
        Assert.Equal(BatteryChargeState.Offline,
            LogitechBatteryDecoding.DecodeSuperstrike(new byte[] { 15, 0, 0x05 }).ChargeState);
        Assert.Throws<BatteryAdapterException>(() => LogitechBatteryDecoding.DecodeSuperstrike(new byte[] { 101, 0, 0 }));
        Assert.Throws<BatteryAdapterException>(() => LogitechBatteryDecoding.DecodeCenturion(Array.Empty<byte>()));
    }

    [Theory]
    [InlineData(0x00, BatteryChargeState.NotCharging)]
    [InlineData(0x01, BatteryChargeState.Charging)]
    [InlineData(0x02, BatteryChargeState.Charging)]
    [InlineData(0x03, BatteryChargeState.NotCharging)]
    [InlineData(0x04, BatteryChargeState.Charging)]
    public void BatteryStatusFeatureMapsChargingStatuses(byte status, BatteryChargeState expected)
    {
        var reading = LogitechBatteryDecoding.DecodeBatteryStatus(new byte[] { 77, 50, status });

        Assert.Equal(77, reading.Percentage);
        Assert.Equal(expected, reading.ChargeState);
    }

    [Theory]
    [InlineData(0x05, BatteryAdapterErrorKind.BatteryReportedError)]
    [InlineData(0x06, BatteryAdapterErrorKind.BatteryReportedError)]
    [InlineData(0x07, BatteryAdapterErrorKind.BatteryReportedError)]
    [InlineData(0x08, BatteryAdapterErrorKind.MalformedResponse)]
    public void BatteryStatusFeatureRejectsErrorAndUndefinedStatuses(byte status, BatteryAdapterErrorKind expected)
    {
        var exception = Assert.Throws<BatteryAdapterException>(() =>
            LogitechBatteryDecoding.DecodeBatteryStatus(new byte[] { 0, 0, status }));
        Assert.Equal(expected, exception.Kind);
    }

    [Fact]
    public void BatteryStatusFeatureRejectsInvalidLevelsAndTruncatedReplies()
    {
        Assert.Equal(BatteryAdapterErrorKind.MalformedResponse, Assert.Throws<BatteryAdapterException>(() =>
            LogitechBatteryDecoding.DecodeBatteryStatus(new byte[] { 101, 0, 0 })).Kind);
        Assert.Equal(BatteryAdapterErrorKind.MalformedResponse, Assert.Throws<BatteryAdapterException>(() =>
            LogitechBatteryDecoding.DecodeBatteryStatus(new byte[] { 50, 0 })).Kind);
    }

    [Theory]
    [InlineData(new byte[] { 64, 0x08, 0x00, 0x00 }, true)]
    [InlineData(new byte[] { 100, 0x08, 0x03, 0x01 }, true)]
    [InlineData(new byte[] { 15, 0x04, 0x05, 0x00 }, true)]
    [InlineData(new byte[] { 0, 0x04, 0x01, 0x00 }, false)]
    public void UnifiedBatteryDecodingMatchesTheSuperstrikeDecoder(byte[] parameters, bool supportsPercentage)
    {
        Assert.Equal(
            LogitechBatteryDecoding.DecodeSuperstrike(parameters, supportsPercentage),
            LogitechBatteryDecoding.DecodeUnifiedBattery(parameters, supportsPercentage));
    }

    [Fact]
    public void DeviceInformationListsEveryModelIdWithItsTransport()
    {
        // Entity count, unit ID (4), reserved, transport bitmap (BLE + eQuad), model IDs in transport order.
        var parameters = new byte[] { 3, 1, 2, 3, 4, 0, 0x06, 0xB0, 0x34, 0x40, 0xBD };

        var ids = LogitechDeviceInformation.GetModelIds(parameters);

        Assert.Equal(new (byte, ushort)[] { (0x02, 0xB034), (0x04, 0x40BD) }, ids);
        Assert.Equal("transport bitmap 0x06, model IDs: transport 0x02=0xB034, transport 0x04=0x40BD",
            LogitechDeviceInformation.FormatModelIds(parameters));
        Assert.Empty(LogitechDeviceInformation.GetModelIds(new byte[] { 3, 1, 2 }));
        Assert.Equal("unavailable", LogitechDeviceInformation.FormatModelIds(new byte[] { 3, 1, 2 }));
    }

    [Fact]
    public void CollectionQueriesMatchTheWholeReceiverIdentity()
    {
        var query = new HidCollectionQuery
        {
            VendorId = 0x046D,
            ProductId = 0x0AF7,
            InterfaceNumber = 3,
            UsagePage = 0xFFA0,
            Usage = 1,
            RequiredReportId = 0x51
        };
        var headsetCollection = new HidCollectionInfo
        {
            DevicePath = "headset",
            VendorId = 0x046D,
            ProductId = 0x0AF7,
            InterfaceNumber = 3,
            UsagePage = 0xFFA0,
            Usage = 1,
            SupportedReportIds = new HashSet<byte> { 0x51 }
        };
        var audioCollection = headsetCollection with { DevicePath = "audio", UsagePage = 1, Usage = 2 };

        Assert.True(query.Matches(headsetCollection));
        Assert.False(query.Matches(audioCollection));
    }

    [Fact]
    public void CenturionFramesValidateLengthAndRoundTripPayload()
    {
        var frame = CenturionClient.BuildFrame(new byte[] { 0x03, 0x11, 0x00, 0x03 });
        Assert.True(CenturionClient.TryExtractPayload(frame, out var payload));
        Assert.Equal(new byte[] { 0x03, 0x11, 0x00, 0x03 }, payload);

        frame[1] = 0xFF;
        Assert.False(CenturionClient.TryExtractPayload(frame, out _));
        Assert.False(CenturionClient.TryExtractPayload(new byte[] { 0x51, 0x01, 0, 0 }, out _));
    }

    [Fact]
    public async Task HidppClientIgnoresUnrelatedResponsesAndCorrelatesSoftwareId()
    {
        var collection = new HidCollectionInfo
        {
            DevicePath = "receiver",
            VendorId = 0x046D,
            ProductId = 0xC54D,
            InputReportLength = 20,
            OutputReportLength = 20,
            SupportedReportIds = new HashSet<byte> { 0x10, 0x11 }
        };
        var transaction = new FixtureTransaction(
            HidppResponse(2, 9, 0, 0, new byte[] { 0x55 }),
            HidppResponse(1, 9, 0, 0, new byte[] { 0x56 }),
            HidppResponse(1, 9, 0, 1, new byte[] { 0x42 }));
        var client = new HidppClient(transaction, collection, new NullBatterioLog());

        var response = await client.SendAsync(1, 9, 0, ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.Equal(new byte[] { 0x42, 0x00, 0x00 }, response.Parameters);
        Assert.Single(transaction.Writes);
        Assert.Equal(0x10, transaction.Writes[0][0]);
    }

    [Fact]
    public async Task CenturionClientDiscoversBridgeAndBatteryFeatureBeforeReading()
    {
        var collection = new HidCollectionInfo
        {
            DevicePath = "headset",
            VendorId = LogitechGProX2LightspeedAdapter.VendorId,
            ProductId = LogitechGProX2LightspeedAdapter.ProductId,
            InterfaceNumber = LogitechGProX2LightspeedAdapter.InterfaceNumber,
            UsagePage = LogitechGProX2LightspeedAdapter.UsagePage,
            Usage = LogitechGProX2LightspeedAdapter.Usage,
            InputReportLength = 64,
            OutputReportLength = 64,
            SupportedReportIds = new HashSet<byte> { 0x51 }
        };
        var transaction = new FixtureTransaction(ModernResponses());
        var client = new CenturionClient(transaction, collection, new NullBatterioLog());

        var reading = await client.GetBatteryAsync(CancellationToken.None);

        Assert.Equal(15, reading.Percentage);
        Assert.Equal(BatteryChargeState.Charging, reading.ChargeState);
        Assert.Equal(11, transaction.Writes.Count);
        Assert.All(transaction.Writes, write => Assert.Equal(0x51, write[0]));
    }

    [Fact]
    public async Task LightspeedLegacyFallbackRequiresAnExplicitModernUnsupportedResponse()
    {
        var collection = HeadsetCollection();
        var legacyResponse = new byte[64];
        legacyResponse[0] = 0x51;
        legacyResponse[1] = 0x0B;
        legacyResponse[8] = 0x04;
        legacyResponse[10] = 15;
        legacyResponse[12] = 0x02;
        var transaction = new FixtureTransaction(
            CenturionResponse(new byte[] { 0xFF, 0, 1, 0x09 }),
            legacyResponse);
        var adapter = new LogitechGProX2LightspeedAdapter(new FixtureProvider(collection, transaction));

        var reading = await adapter.GetBatteryStatus();

        Assert.Equal(15, reading.Percentage);
        Assert.Equal(BatteryChargeState.Charging, reading.ChargeState);
        Assert.Equal(2, transaction.Writes.Count);
        Assert.Equal(new byte[] { 0x51, 0x08, 0x00, 0x03, 0x1A, 0x00, 0x03, 0x00, 0x04, 0x0A }, transaction.Writes[1][..10]);
    }

    [Fact]
    public async Task LightspeedDoesNotUseLegacyFallbackAfterMalformedModernResponse()
    {
        var transaction = new FixtureTransaction(new byte[] { 0x51, 0x01, 0x00, 0x00 });
        var adapter = new LogitechGProX2LightspeedAdapter(new FixtureProvider(HeadsetCollection(), transaction));

        var exception = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());

        Assert.Equal(BatteryAdapterErrorKind.MalformedResponse, exception.Kind);
        Assert.Single(transaction.Writes);
    }

    [Fact]
    public async Task SuperstrikeAdapterIdentifiesThePairedModelAndReadsUnifiedBattery()
    {
        var collection = new HidCollectionInfo
        {
            DevicePath = "mouse-receiver",
            VendorId = LogitechGProX2SuperstrikeAdapter.VendorId,
            ProductId = LogitechGProX2SuperstrikeAdapter.ReceiverProductId,
            InterfaceNumber = LogitechGProX2SuperstrikeAdapter.InterfaceNumber,
            UsagePage = 0xFF00,
            InputReportLength = 20,
            OutputReportLength = 20,
            SupportedReportIds = new HashSet<byte> { 0x10, 0x11 }
        };
        var transaction = new FixtureTransaction(
            HidppResponse(1, 0, 0x10, 1, new byte[] { 2, 0, 0xAA }),
            HidppResponse(1, 0, 0, 1, new byte[] { 3, 0, 0 }),
            HidppResponse(1, 3, 0, 1, new byte[] { 0, 0, 0, 0, 0, 0, 0x04, 0x40, 0xBD, 0, 0, 0, 0 }, 0x11),
            HidppResponse(1, 0, 0, 1, new byte[] { 9, 0, 0 }),
            HidppResponse(1, 9, 0, 1, new byte[] { 0x0F, 0x03, 0 }),
            HidppResponse(1, 9, 0x10, 1, new byte[] { 15, 0, 0x02 }));
        var adapter = new LogitechGProX2SuperstrikeAdapter(new FixtureProvider(collection, transaction));

        var reading = await adapter.GetBatteryStatus();

        Assert.Equal(15, reading.Percentage);
        Assert.Equal(BatteryChargeState.Charging, reading.ChargeState);
        Assert.Equal(6, transaction.Writes.Count);
        Assert.Equal(new byte[] { 0x10, 1, 0, 0x11, 0, 0 }, transaction.Writes[0][..6]);
        Assert.Equal(new byte[] { 0x10, 1, 0, 1, 0, 3, 0 }, transaction.Writes[1][..7]);
        Assert.Equal(new byte[] { 0x10, 1, 3, 1, 0, 0, 0 }, transaction.Writes[2][..7]);
        Assert.Equal(new byte[] { 0x10, 1, 0, 1, 0x10, 4, 0 }, transaction.Writes[3][..7]);
        Assert.Equal(new byte[] { 0x10, 1, 9, 1, 0, 0, 0 }, transaction.Writes[4][..7]);
        Assert.Equal(new byte[] { 0x10, 0x01, 0x09, 0x11, 0, 0, 0 }, transaction.Writes[^1][..7]);
    }

    private static byte[][] ModernResponses() => new byte[][]
    {

            CenturionResponse(new byte[] { 0, 1, 1 }),
            CenturionResponse(new byte[] { 1, 1, 3 }),
            CenturionResponse(new byte[] { 1, 0x11, 0, 0, 0 }),
            CenturionResponse(new byte[] { 1, 0x11, 0, 0, 1 }),
            CenturionResponse(new byte[] { 1, 0x11, 0, 0, 3 }),
            BridgeResponse(0, 1, 1),
            BridgeResponse(1, 1, 3),
            BridgeResponse(1, 0x11, 0, 0, 0),
            BridgeResponse(1, 0x11, 0, 0, 1),
            BridgeResponse(1, 0x11, 0, 1, 4),
            BridgeResponse(2, 1, 15, 0, 2)
    };

    private static byte[] HidppResponse(byte device, byte feature, byte function, byte softwareId, byte[] parameters, byte reportId = 0x10)
    {
        var report = new byte[20];
        report[0] = reportId;
        report[1] = device;
        report[2] = feature;
        report[3] = (byte)((function & 0xF0) | softwareId);
        parameters.CopyTo(report, 4);
        return report;
    }

    private static byte[] CenturionResponse(byte[] payload)
    {
        var frame = new byte[64];
        frame[0] = 0x51;
        frame[1] = (byte)(payload.Length + 1);
        payload.CopyTo(frame, 3);
        return frame;
    }

    private static byte[] BridgeResponse(byte feature, byte functionSoftware, params byte[] data)
    {
        var payload = new byte[7 + data.Length];
        payload[0] = 2;
        payload[1] = 0x10;
        payload[3] = (byte)(3 + data.Length);
        payload[5] = feature;
        payload[6] = functionSoftware;
        data.CopyTo(payload, 7);
        return CenturionResponse(payload);
    }

    private static HidCollectionInfo HeadsetCollection() => new()
    {
        DevicePath = "headset",
        VendorId = LogitechGProX2LightspeedAdapter.VendorId,
        ProductId = LogitechGProX2LightspeedAdapter.ProductId,
        InterfaceNumber = LogitechGProX2LightspeedAdapter.InterfaceNumber,
        UsagePage = LogitechGProX2LightspeedAdapter.UsagePage,
        Usage = LogitechGProX2LightspeedAdapter.Usage,
        InputReportLength = 64,
        OutputReportLength = 64,
        SupportedReportIds = new HashSet<byte> { 0x51 }
    };

    private sealed class FixtureTransaction(params byte[][] responses) : IHidReportTransaction
    {
        private readonly Queue<byte[]> _responses = new(responses);

        public List<byte[]> Writes { get; } = new();

        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default)
        {
            Writes.Add(report.ToArray());
            return ValueTask.CompletedTask;
        }

        public async ValueTask<int> ReadAsync(Memory<byte> report, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_responses.Count == 0) await Task.Delay(Timeout.Infinite, cancellationToken);
            var response = _responses.Dequeue();
            // The protocol ping echoes the random challenge; the rest of this response is fixed wire data.
            if (response[0] == 0x10 && response[2] == 0 && response[3] == 0x11 && response[6] == 0xAA)
                response[6] = Writes[^1][6];
            response.CopyTo(report);
            return response.Length;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixtureProvider(HidCollectionInfo collection, FixtureTransaction transaction) : IHidCollectionProvider
    {
        public IReadOnlyList<HidCollectionInfo> Enumerate(HidCollectionQuery query) =>
            query.Matches(collection) ? new[] { collection } : Array.Empty<HidCollectionInfo>();

        public ValueTask<IHidReportTransport> OpenAsync(HidCollectionInfo collection, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IHidReportTransport>(new FixtureTransport(collection, transaction));
    }

    private sealed class FixtureTransport(HidCollectionInfo collection, FixtureTransaction transaction) : IHidReportTransport
    {
        public HidCollectionInfo Collection { get; } = collection;

        public ValueTask<IHidReportTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IHidReportTransaction>(transaction);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

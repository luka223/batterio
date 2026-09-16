using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

public sealed partial class ProtocolTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task CenturionNonUnsupportedErrorsNeverFallBack(byte code)
    {
        var transaction = new FixtureTransaction(CenturionResponse(new byte[] { 0xFF, 0, 1, code }));
        await using var adapter = new LogitechGProX2LightspeedAdapter(new FixtureProvider(HeadsetCollection(), transaction));
        var error = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());
        Assert.Equal(code == 8 ? BatteryAdapterErrorKind.DeviceBusy : BatteryAdapterErrorKind.ProtocolError, error.Kind);
        Assert.Single(transaction.Writes);
    }

    [Fact]
    public async Task UnrelatedCenturionErrorDoesNotTriggerFallback()
    {
        var transaction = new FixtureTransaction(new[] { CenturionResponse(new byte[] { 0xFF, 9, 1, 9 }) }
            .Concat(ModernResponses()).ToArray());
        await using var adapter = new LogitechGProX2LightspeedAdapter(new FixtureProvider(HeadsetCollection(), transaction));
        Assert.Equal(15, (await adapter.GetBatteryStatus()).Percentage);
        Assert.Equal(11, transaction.Writes.Count);
    }

    [Theory]
    [InlineData(0x11)]
    [InlineData(0)]
    [InlineData(2)]
    public async Task BridgeIgnoresWrongFunctionOrSoftwareId(byte functionSoftware)
    {
        var frames = ModernResponses().ToList();
        frames.Insert(10, BridgeResponse(2, functionSoftware, 90, 0, 2));
        var transaction = new FixtureTransaction(frames.ToArray());
        await using var adapter = new LogitechGProX2LightspeedAdapter(new FixtureProvider(HeadsetCollection(), transaction));
        Assert.Equal(15, (await adapter.GetBatteryStatus()).Percentage);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidNestedLengthAndFragmentationFailWithoutFallback(bool fragment)
    {
        var frames = ModernResponses();
        if (fragment) frames[10][2] = 1;
        else frames[10][6] = 2; // Declared nested length differs from bytes carried.
        var transaction = new FixtureTransaction(frames);
        await using var adapter = new LogitechGProX2LightspeedAdapter(new FixtureProvider(HeadsetCollection(), transaction));
        var error = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());
        Assert.Equal(BatteryAdapterErrorKind.MalformedResponse, error.Kind);
        Assert.Equal(11, transaction.Writes.Count);
    }

    [Fact]
    public async Task NestedBusyErrorDoesNotFallBack()
    {
        var frames = ModernResponses();
        frames[10] = BridgeResponse(0xFF, 2, 1, 8);
        var transaction = new FixtureTransaction(frames);
        await using var adapter = new LogitechGProX2LightspeedAdapter(new FixtureProvider(HeadsetCollection(), transaction));
        var error = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());
        Assert.Equal(BatteryAdapterErrorKind.DeviceBusy, error.Kind);
        Assert.Equal(11, transaction.Writes.Count);
    }

    [Fact]
    public async Task AckOnlyTimesOutWithoutFallback()
    {
        var frames = ModernResponses();
        frames[10] = CenturionResponse(new byte[] { 2, 0x11 });
        var transaction = new FixtureTransaction(frames);
        await using var adapter = new LogitechGProX2LightspeedAdapter(new FixtureProvider(HeadsetCollection(), transaction));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetBatteryStatus(timeout.Token));
        Assert.Equal(11, transaction.Writes.Count);
    }

    [Fact]
    public async Task EmptyRootReplyIsMalformedRatherThanUnsupported()
    {
        var transaction = new FixtureTransaction(CenturionResponse(new byte[] { 0, 1 }));
        await using var adapter = new LogitechGProX2LightspeedAdapter(new FixtureProvider(HeadsetCollection(), transaction));
        var error = await Assert.ThrowsAsync<BatteryAdapterException>(() => adapter.GetBatteryStatus());
        Assert.Equal(BatteryAdapterErrorKind.MalformedResponse, error.Kind);
        Assert.Single(transaction.Writes);
    }
}

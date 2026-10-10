using Batterio.Core;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

public sealed class SteelSeriesOmniDecodingTests
{
    // Layout measured on a real hub: 01 B0 .. [6]=headset [7]=reserve .. [0x0E]=link [0x0F]=charging.
    internal static byte[] Frame(byte headset, byte reserve, byte link = 0x08, byte charging = 0x08, int length = 64)
    {
        var frame = new byte[length];
        frame[0] = 0x01;
        frame[1] = 0xB0;
        frame[0x06] = headset;
        frame[0x07] = reserve;
        frame[0x0E] = link;
        frame[0x0F] = charging;
        return frame;
    }

    [Fact]
    public void OnlineDischargingHeadsetAndChargingReserveAreDecoded()
    {
        var reading = SteelSeriesNovaProOmniStatusDecoding.Decode(Frame(97, 81));

        Assert.Equal(new BatteryReading(97, BatteryChargeState.NotCharging), reading with { Reserve = null });
        Assert.Equal(new BatteryReading(81, BatteryChargeState.Charging), reading.Reserve);
    }

    [Fact]
    public void HeadsetChargingFlagIsMappedToCharging()
    {
        var reading = SteelSeriesNovaProOmniStatusDecoding.Decode(Frame(55, 100, charging: 0x02));

        Assert.Equal(BatteryChargeState.Charging, reading.ChargeState);
        Assert.Equal(55, reading.Percentage);
    }

    [Fact]
    public void PluggedInAtOneHundredPercentStillShowsTheChargingBolt()
    {
        var reading = SteelSeriesNovaProOmniStatusDecoding.Decode(Frame(100, 100, charging: 0x04));

        Assert.Equal(BatteryChargeState.Charging, reading.ChargeState);
    }

    [Fact]
    public void PluggedInBelowOneHundredPercentIsNotCharging()
    {
        var reading = SteelSeriesNovaProOmniStatusDecoding.Decode(Frame(80, 100, charging: 0x04));

        Assert.Equal(BatteryChargeState.NotCharging, reading.ChargeState);
        Assert.Equal(80, reading.Percentage);
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x02)]
    [InlineData(0x04)]
    public void HeadsetIsOfflineUnlessTheLinkIsConnectedAndTheReserveStaysLive(byte link)
    {
        var reading = SteelSeriesNovaProOmniStatusDecoding.Decode(Frame(50, 60, link));

        Assert.Equal(BatteryChargeState.Offline, reading.ChargeState);
        Assert.Null(reading.Percentage);
        Assert.Equal(new BatteryReading(60, BatteryChargeState.Charging), reading.Reserve);
    }

    [Fact]
    public void FullReserveIsNotCharging()
    {
        var reading = SteelSeriesNovaProOmniStatusDecoding.Decode(Frame(40, 100));

        Assert.Equal(new BatteryReading(100, BatteryChargeState.NotCharging), reading.Reserve);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(0xFF)]
    public void AnEmptyReserveSlotHasNoReserveReading(byte sentinel)
    {
        var reading = SteelSeriesNovaProOmniStatusDecoding.Decode(Frame(40, sentinel));

        Assert.Null(reading.Reserve);
        Assert.Equal(40, reading.Percentage);
    }

    [Fact]
    public void AOnePercentReserveStillDecodesAsCharging()
    {
        var reading = SteelSeriesNovaProOmniStatusDecoding.Decode(Frame(40, 1));

        Assert.Equal(new BatteryReading(1, BatteryChargeState.Charging), reading.Reserve);
    }

    [Fact]
    public void HeadsetPercentageAboveOneHundredIsMalformed()
    {
        var error = Assert.Throws<BatteryAdapterException>(() => SteelSeriesNovaProOmniStatusDecoding.Decode(Frame(101, 50)));

        Assert.Equal(BatteryAdapterErrorKind.MalformedResponse, error.Kind);
    }

    [Fact]
    public void AnOfflineHeadsetMayReportAnyHeadsetByte()
    {
        var reading = SteelSeriesNovaProOmniStatusDecoding.Decode(Frame(0xFF, 50, link: 0x04));

        Assert.Equal(BatteryChargeState.Offline, reading.ChargeState);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(16)]
    public void ShortFramesAreMalformed(int length)
    {
        var truncated = Frame(50, 50)[..length];

        var error = Assert.Throws<BatteryAdapterException>(() => SteelSeriesNovaProOmniStatusDecoding.Decode(truncated));

        Assert.Equal(BatteryAdapterErrorKind.MalformedResponse, error.Kind);
    }

    [Fact]
    public void FramesThatAreNotStatusRepliesAreMalformedAndNotReplies()
    {
        var eventFrame = Frame(50, 50);
        eventFrame[0] = 0x07;
        eventFrame[1] = 0xB7;

        Assert.False(SteelSeriesNovaProOmniStatusDecoding.IsStatusReply(eventFrame));
        Assert.True(SteelSeriesNovaProOmniStatusDecoding.IsStatusReply(Frame(50, 50)));
        Assert.Equal(BatteryAdapterErrorKind.MalformedResponse,
            Assert.Throws<BatteryAdapterException>(() => SteelSeriesNovaProOmniStatusDecoding.Decode(eventFrame)).Kind);
    }

    [Fact]
    public void StatusRequestIsReportOneOpcodeB0PaddedWithZeros()
    {
        var request = SteelSeriesNovaProOmniStatusDecoding.CreateStatusRequest(64);

        Assert.Equal(64, request.Length);
        Assert.Equal(0x01, request[0]);
        Assert.Equal(0xB0, request[1]);
        Assert.All(request.Skip(2), value => Assert.Equal(0, value));
    }
}

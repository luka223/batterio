using Batterio.Core;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

public sealed class KeychronBatteryReportDecodingTests
{
    /// <summary>Report ID 0, then A4, percentage, millivolts (little-endian), charge, connection, model; padded to 33 bytes.</summary>
    internal static byte[] Frame(byte percentage = 100, byte charge = 0, byte model = 2, ushort millivolts = 4134)
    {
        var frame = new byte[33];
        frame[1] = 0xA4;
        frame[2] = percentage;
        frame[3] = (byte)(millivolts & 0xFF);
        frame[4] = (byte)(millivolts >> 8);
        frame[5] = charge;
        frame[6] = 0x04;
        frame[7] = model;
        return frame;
    }

    [Fact]
    public void TheCapturedHardwareFrameDecodes()
    {
        byte[] frame = [0x00, 0xA4, 0x64, 0x26, 0x10, 0x02, 0x04, 0x02];

        Assert.True(KeychronBatteryReportDecoding.IsQ1HeBatteryReport(frame));
        Assert.Equal(new BatteryReading(100, BatteryChargeState.NotCharging), KeychronBatteryReportDecoding.Decode(frame));
        Assert.Equal(4134, KeychronBatteryReportDecoding.DecodeMillivolts(frame));
    }

    [Theory]
    [InlineData(0, BatteryChargeState.NotCharging)]
    [InlineData(1, BatteryChargeState.Charging)]
    [InlineData(2, BatteryChargeState.NotCharging)]
    [InlineData(9, BatteryChargeState.NotCharging)]
    public void TheChargeByteIsMapped(byte charge, BatteryChargeState expected)
    {
        var reading = KeychronBatteryReportDecoding.Decode(Frame(55, charge));

        Assert.Equal(55, reading.Percentage);
        Assert.Equal(expected, reading.ChargeState);
    }

    [Fact]
    public void AnUnknownChargeByteIsLoggedAsAWarning()
    {
        var log = new RecordingLog();

        KeychronBatteryReportDecoding.Decode(Frame(charge: 9), log);

        Assert.Contains(log.Entries, entry => entry.Level == BatterioLogLevel.Warning);
    }

    [Fact]
    public void OtherFramesAreNotReports()
    {
        var wrongCommand = Frame();
        wrongCommand[1] = 0xA5;

        Assert.False(KeychronBatteryReportDecoding.IsQ1HeBatteryReport(wrongCommand));
        Assert.False(KeychronBatteryReportDecoding.IsQ1HeBatteryReport(Frame(model: 1)));
        Assert.False(KeychronBatteryReportDecoding.IsQ1HeBatteryReport(Frame().AsSpan(0, 7)));
        Assert.False(KeychronBatteryReportDecoding.IsQ1HeBatteryReport([]));
        var error = Assert.Throws<BatteryAdapterException>(() => KeychronBatteryReportDecoding.Decode(Frame(model: 1)));
        Assert.Equal(BatteryAdapterErrorKind.MalformedResponse, error.Kind);
    }

    [Fact]
    public void AnImpossiblePercentageIsMalformed()
    {
        var error = Assert.Throws<BatteryAdapterException>(() => KeychronBatteryReportDecoding.Decode(Frame(101)));

        Assert.Equal(BatteryAdapterErrorKind.MalformedResponse, error.Kind);
    }

    private sealed class RecordingLog : IBatterioLog
    {
        public List<(BatterioLogLevel Level, string Message)> Entries { get; } = new();

        public void Log(BatterioLogLevel level, string message, Exception? exception = null) => Entries.Add((level, message));
    }
}

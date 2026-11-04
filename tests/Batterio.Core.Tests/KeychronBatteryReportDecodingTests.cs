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

        Assert.Equal(expected, reading.ChargeState);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void OffTheChargerTheFirmwarePercentageIsUntouched(byte charge)
    {
        Assert.Equal(27, KeychronBatteryReportDecoding.Decode(Frame(27, charge, millivolts: 3555)).Percentage);
        Assert.Equal(100, KeychronBatteryReportDecoding.Decode(Frame(100, charge, millivolts: 4101)).Percentage);
    }

    [Theory]
    [InlineData(3300, 0)]
    [InlineData(3400, 10)]
    [InlineData(3555, 27.33)]
    [InlineData(3742, 52.27)]
    [InlineData(4100, 100)]
    [InlineData(4147, 100)]
    public void TheFirmwareVoltageLineIsReproduced(int millivolts, double expected)
    {
        Assert.Equal(expected, KeychronBatteryReportDecoding.PercentageFromMillivolts(millivolts), 2);
    }

    // Readings logged on 2026-11-04 while charging from 27% (3555 mV on battery).
    [Theory]
    [InlineData(3742, 52, 25)]
    [InlineData(3901, 73, 46)]
    [InlineData(4003, 87, 60)]
    [InlineData(4101, 100, 73)]
    [InlineData(4122, 100, 85)]
    [InlineData(4141, 100, 96)]
    [InlineData(4147, 100, 99)]
    [InlineData(4200, 100, 99)]
    public void WhileChargingThePercentageComesFromTheCorrectedVoltage(int millivolts, byte firmware, int expected)
    {
        var reading = KeychronBatteryReportDecoding.Decode(Frame(firmware, charge: 1, millivolts: (ushort)millivolts));

        Assert.Equal(new BatteryReading(expected, BatteryChargeState.Charging), reading);
    }

    [Fact]
    public void TheChargingLiftTapersNearTheTop()
    {
        Assert.Equal(200, KeychronBatteryReportDecoding.ChargingLift(3742));
        Assert.Equal(200, KeychronBatteryReportDecoding.ChargingLift(4100));
        Assert.Equal(46, KeychronBatteryReportDecoding.ChargingLift(4147));
        Assert.Equal(46, KeychronBatteryReportDecoding.ChargingLift(4300));
        Assert.InRange(KeychronBatteryReportDecoding.ChargingLift(4124), 46, 200);
    }

    [Fact]
    public void ChargingNeverReachesAHundredBeforeTheFullByte()
    {
        for (var millivolts = 3300; millivolts <= 4400; millivolts++)
        {
            Assert.InRange(KeychronBatteryReportDecoding.EstimateChargingPercentage((ushort)millivolts), 0, 99);
        }
    }

    [Fact]
    public void TheChargingRiseIsLimitedAndNeverFalls()
    {
        var fiveMinutes = TimeSpan.FromMinutes(5);

        Assert.Equal(27.6, KeychronBatteryReportDecoding.LimitChargingRise(37, 25.6, fiveMinutes), 6);
        Assert.Equal(40, KeychronBatteryReportDecoding.LimitChargingRise(39, 40, fiveMinutes));
        Assert.Equal(41, KeychronBatteryReportDecoding.LimitChargingRise(41, 40, fiveMinutes));
        Assert.Equal(40, KeychronBatteryReportDecoding.LimitChargingRise(60, 40, TimeSpan.FromMinutes(-1)));
        Assert.Equal(99, KeychronBatteryReportDecoding.LimitChargingRise(99, 98.5, TimeSpan.FromHours(1)));
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

using Batterio.Core;

namespace Batterio.Core.Tests;

public sealed class BatteryPresentationTests
{
    [Theory]
    [InlineData(0, 0, "#EF4444")]
    [InlineData(9, 0, "#EF4444")]
    [InlineData(10, 1, "#EF4444")]
    [InlineData(19, 1, "#EF4444")]
    [InlineData(20, 2, "#EAB308")]
    [InlineData(40, 4, "#EAB308")]
    [InlineData(41, 4, "#22C55E")]
    [InlineData(100, 10, "#22C55E")]
    public void NotChargingPresentationUsesThePercentageBoundaries(int percentage, int filledSegments, string color)
    {
        var presentation = BatteryPresentationRules.Create(
            new BatteryReading(percentage, BatteryChargeState.NotCharging));

        Assert.Equal(BatteryChargeState.NotCharging, presentation.State);
        Assert.Equal(percentage.ToString() + "%", presentation.PercentageText);
        Assert.True(presentation.IsPercentageVisible);
        Assert.Equal(filledSegments, presentation.FilledSegments);
        Assert.Equal(color, presentation.BatteryColorHex);
        Assert.False(presentation.IsCharging);
        Assert.False(presentation.IsOffline);
    }

    [Fact]
    public void ChargingIsAlwaysFullGreenAndKeepsItsReportedPercentage()
    {
        var presentation = BatteryPresentationRules.Create(
            new BatteryReading(9, BatteryChargeState.Charging));

        Assert.Equal(9, presentation.Percentage);
        Assert.Equal(BatteryPresentationRules.ChargingColorHex, presentation.BatteryColorHex);
        Assert.Equal(0, presentation.FilledSegments);
        Assert.True(presentation.IsCharging);
        Assert.False(presentation.IsOffline);
    }

    [Fact]
    public void OfflineHidesPercentageAndUsesAnEmptyGrayBattery()
    {
        var presentation = BatteryPresentationRules.Create(BatteryReading.Offline);

        Assert.Equal(BatteryChargeState.Offline, presentation.State);
        Assert.False(presentation.IsPercentageVisible);
        Assert.Equal(string.Empty, presentation.PercentageText);
        Assert.Equal(0, presentation.FilledSegments);
        Assert.Equal(BatteryPresentationRules.OfflineColorHex, presentation.BatteryColorHex);
        Assert.False(presentation.IsCharging);
        Assert.True(presentation.IsOffline);
    }

    [Fact]
    public void StatusOnlyNotChargingKeepsNormalDevicePresentationButUsesEmptyGrayBattery()
    {
        var presentation = BatteryPresentationRules.Create(
            new BatteryReading(null, BatteryChargeState.NotCharging));

        Assert.Equal(BatteryChargeState.NotCharging, presentation.State);
        Assert.False(presentation.IsPercentageVisible);
        Assert.Equal(0, presentation.FilledSegments);
        Assert.Equal(BatteryPresentationRules.OfflineColorHex, presentation.BatteryColorHex);
        Assert.False(presentation.IsOffline);
    }

    [Fact]
    public void AccessibilityDescriptionContainsOnlyStateAndOptionalPercentage()
    {
        Assert.Equal(
            "Mouse: Charging, 15 percent",
            BatteryPresentationRules.GetAccessibilityDescription(
                "Mouse", new BatteryReading(15, BatteryChargeState.Charging)));
        Assert.Equal(
            "Headset: Not charging",
            BatteryPresentationRules.GetAccessibilityDescription(
                "Headset", new BatteryReading(null, BatteryChargeState.NotCharging)));
        Assert.Equal(
            "Mouse: Offline",
            BatteryPresentationRules.GetAccessibilityDescription("Mouse", BatteryReading.Offline));
    }
}

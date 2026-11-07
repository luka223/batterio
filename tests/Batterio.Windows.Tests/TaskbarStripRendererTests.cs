using System.Drawing;
using Batterio.App.Windows;
using Batterio.Core;

namespace Batterio.Windows.Tests;

public sealed class TaskbarStripRendererTests
{
    private const int TaskbarHeight = 48;

    [Fact]
    public void EmptyItemListRendersNothing()
    {
        Assert.Null(TaskbarStripRenderer.Render([], 1, TaskbarHeight, lightTaskbar: false));
        Assert.Equal(0, TaskbarStripRenderer.MeasureWidth([], 1));
    }

    [Fact]
    public void WidthGrowsWithEveryItem()
    {
        var one = TaskbarStripRenderer.MeasureWidth([Item("a", 50)], 1);
        var two = TaskbarStripRenderer.MeasureWidth([Item("a", 50), Item("b", 50)], 1);
        var three = TaskbarStripRenderer.MeasureWidth([Item("a", 50), Item("b", 50), Item("c", 50)], 1);

        Assert.True(one > 0);
        Assert.True(two > one);
        Assert.True(three > two);
    }

    [Fact]
    public void BoltAddsWidthOnlyWhileCharging()
    {
        var idle = TaskbarStripRenderer.MeasureWidth([Item("a", 50)], 1);
        var charging = TaskbarStripRenderer.MeasureWidth([Item("a", 50, charging: true)], 1);

        Assert.True(charging > idle);
    }

    [Fact]
    public void RenderedBitmapMatchesTheMeasuredWidthAndRequestedHeight()
    {
        var items = new[] { Item("a", 80), Item("b", 7, charging: true) };

        using var bitmap = TaskbarStripRenderer.Render(items, 1, TaskbarHeight, lightTaskbar: false);

        Assert.NotNull(bitmap);
        Assert.Equal(TaskbarStripRenderer.MeasureWidth(items, 1), bitmap.Width);
        Assert.Equal(TaskbarHeight, bitmap.Height);
    }

    [Fact]
    public void LargerDisplayScaleProducesAWiderStrip()
    {
        var items = new[] { Item("a", 64) };

        Assert.True(
            TaskbarStripRenderer.MeasureWidth(items, 1.5) > TaskbarStripRenderer.MeasureWidth(items, 1));
    }

    [Fact]
    public void EveryPixelIsAtLeastNearlyTransparentButNeverFullyTransparent()
    {
        using var bitmap = TaskbarStripRenderer.Render([Item("a", 64)], 1, TaskbarHeight, lightTaskbar: false)!;

        // Alpha 0 would let clicks fall through to the taskbar underneath the gaps.
        Assert.True(bitmap.GetPixel(0, 0).A >= 1);
        Assert.True(bitmap.GetPixel(bitmap.Width - 1, TaskbarHeight - 1).A >= 1);
    }

    [Fact]
    public void DarkTaskbarDrawsLightText()
    {
        using var bitmap = TaskbarStripRenderer.Render([Item("a", 64)], 1, TaskbarHeight, lightTaskbar: false)!;

        var average = AverageBrightnessOfOpaquePixels(bitmap);

        Assert.True(average > 200, $"Expected near-white text on a dark taskbar but the average brightness was {average}.");
    }

    [Fact]
    public void LightTaskbarDrawsDarkText()
    {
        using var bitmap = TaskbarStripRenderer.Render([Item("a", 64)], 1, TaskbarHeight, lightTaskbar: true)!;

        var average = AverageBrightnessOfOpaquePixels(bitmap);

        Assert.True(average < 60, $"Expected near-black text on a light taskbar but the average brightness was {average}.");
    }

    [Fact]
    public void ChargingItemsDrawAnAmberBolt()
    {
        using var idle = TaskbarStripRenderer.Render([Item("a", 64)], 1, TaskbarHeight, lightTaskbar: false)!;
        using var charging = TaskbarStripRenderer.Render([Item("a", 64, charging: true)], 1, TaskbarHeight, lightTaskbar: false)!;

        Assert.Equal(0, CountAmberPixels(idle));
        Assert.True(CountAmberPixels(charging) > 10, "The lightning bolt should be drawn in amber.");
    }

    [Fact]
    public void OfflineItemsShowDashesInsteadOfAPercentageAndNeverDrawABolt()
    {
        Assert.Equal("--", TaskbarStripRenderer.FormatPercentage(null));
        Assert.Equal("64%", TaskbarStripRenderer.FormatPercentage(64));

        var offline = new TaskbarStripItem("a", DeviceKind.Mouse, null, IsCharging: false);
        var offlineFlaggedCharging = new TaskbarStripItem("a", DeviceKind.Mouse, null, IsCharging: true);

        Assert.Equal(
            TaskbarStripRenderer.MeasureWidth([offline], 1),
            TaskbarStripRenderer.MeasureWidth([offlineFlaggedCharging], 1));

        using var bitmap = TaskbarStripRenderer.Render([offlineFlaggedCharging], 1, TaskbarHeight, lightTaskbar: false)!;
        Assert.Equal(0, CountAmberPixels(bitmap));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OfflineItemsAreDrawnDimmedComparedWithOnlineItems(bool lightTaskbar)
    {
        using var online = TaskbarStripRenderer.Render([Item("a", 64)], 1, TaskbarHeight, lightTaskbar)!;
        using var offline = TaskbarStripRenderer.Render(
            [new TaskbarStripItem("a", DeviceKind.Mouse, null, IsCharging: false)], 1, TaskbarHeight, lightTaskbar)!;

        Assert.True(MaxAlpha(online) >= 200, "Online items should have fully opaque pixels.");
        var offlineMax = MaxAlpha(offline);
        Assert.True(offlineMax > 40, "Offline items must still be visible.");
        Assert.True(offlineMax < 150, $"Offline items should look disabled but the strongest pixel had alpha {offlineMax}.");
    }

    [Theory]
    [InlineData(DeviceKind.Mouse, TaskbarStripRenderer.MouseGlyph)]
    [InlineData(DeviceKind.Keyboard, TaskbarStripRenderer.KeyboardGlyph)]
    [InlineData(DeviceKind.Headset, TaskbarStripRenderer.HeadphoneGlyph)]
    public void EachDeviceKindUsesItsOwnGlyph(DeviceKind kind, string expected)
    {
        Assert.Equal(expected, TaskbarStripRenderer.GlyphFor(kind));
    }

    private static TaskbarStripItem Item(string id, int percentage, bool charging = false) =>
        new(id, DeviceKind.Mouse, percentage, charging);

    private static int MaxAlpha(Bitmap bitmap)
    {
        var max = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                max = Math.Max(max, bitmap.GetPixel(x, y).A);
            }
        }

        return max;
    }

    private static double AverageBrightnessOfOpaquePixels(Bitmap bitmap)
    {
        double total = 0;
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A >= 200)
                {
                    total += (pixel.R + pixel.G + pixel.B) / 3d;
                    count++;
                }
            }
        }

        Assert.True(count > 0, "No sufficiently opaque pixels were drawn.");
        return total / count;
    }

    private static int CountAmberPixels(Bitmap bitmap)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                // The bolt is a thin glyph, so most of its antialiased pixels are only partly opaque.
                if (pixel.A >= 100 && pixel.R > 200 && pixel.G is > 130 and < 220 && pixel.B < 60)
                {
                    count++;
                }
            }
        }

        return count;
    }
}

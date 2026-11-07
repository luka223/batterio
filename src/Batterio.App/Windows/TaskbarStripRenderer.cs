using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Batterio.Core;
using Microsoft.Win32;
using Color = System.Drawing.Color;
using Font = System.Drawing.Font;
using PointF = System.Drawing.PointF;

namespace Batterio.App.Windows;

/// <summary>
/// Draws the taskbar battery strip into a premultiplied 32bpp bitmap with per-pixel alpha.
/// GDI+ is used because plain GDI text writes alpha 0, which a layered window would show as holes.
/// </summary>
internal static class TaskbarStripRenderer
{
    internal const string MouseGlyph = "";
    internal const string KeyboardGlyph = "";
    internal const string HeadphoneGlyph = "";
    internal const string LightningBoltGlyph = "";

    internal static readonly Color BoltColor = Color.FromArgb(0xF5, 0xB4, 0x00);
    internal static readonly Color DarkTaskbarText = Color.White;
    internal static readonly Color LightTaskbarText = Color.FromArgb(0x1A, 0x1A, 0x1A);

    /// <summary>Shown instead of a percentage while a device is offline.</summary>
    internal const string OfflineText = "--";

    /// <summary>Opacity (0-255) of an offline device's glyph and text.</summary>
    internal const int OfflineAlpha = 110;

    private const double GlyphSize = 16;
    private const double BoltSize = 12;
    private const double GlyphToTextGap = 6;
    private const double TextToBoltGap = 2;
    private const double ItemGap = 16;
    private const double EdgePadding = 4;
    private const double TextSize = 14;

    private static readonly string[] IconFontCandidates = ["Segoe Fluent Icons", "Segoe MDL2 Assets"];

    private static readonly string[] TextFontCandidates =
        ["Segoe UI Variable Text Semibold", "Segoe UI Semibold", "Segoe UI"];

    private static readonly Lazy<HashSet<string>> InstalledFamilies = new(() =>
    {
        using var collection = new InstalledFontCollection();
        return new HashSet<string>(collection.Families.Select(family => family.Name), StringComparer.OrdinalIgnoreCase);
    });

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>True when Windows draws the taskbar with the light theme (dark text is needed).</summary>
    public static bool IsLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Width in pixels of the strip for these items; 0 when there is nothing to draw.</summary>
    public static int MeasureWidth(IReadOnlyList<TaskbarStripItem> items, double scale)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return 0;
        }

        using var fonts = StripFonts.Create(scale);
        using var measure = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(measure);
        ConfigureText(graphics);
        return MeasureWidth(items, scale, graphics, fonts);
    }

    /// <summary>
    /// Renders the strip. Returns <c>null</c> when there is nothing to draw. The whole bitmap is
    /// filled with alpha 1 (not 0) so clicks in the gaps between items still reach the window.
    /// </summary>
    public static Bitmap? Render(IReadOnlyList<TaskbarStripItem> items, double scale, int height, bool lightTaskbar)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0 || height <= 0)
        {
            return null;
        }

        using var fonts = StripFonts.Create(scale);
        using var measure = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var measureGraphics = Graphics.FromImage(measure);
        ConfigureText(measureGraphics);
        var width = MeasureWidth(items, scale, measureGraphics, fonts);

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.FromArgb(1, 0, 0, 0));
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            ConfigureText(graphics);

            using var format = CreateFormat();
            var textColor = lightTaskbar ? LightTaskbarText : DarkTaskbarText;
            using var textBrush = new SolidBrush(textColor);
            // Offline devices are drawn in the same colour but mostly transparent, so they look disabled.
            using var dimBrush = new SolidBrush(Color.FromArgb(OfflineAlpha, textColor));
            using var boltBrush = new SolidBrush(BoltColor);

            var x = (float)Px(EdgePadding, scale);
            foreach (var item in items)
            {
                var brush = item.IsOffline ? dimBrush : textBrush;
                var glyphWidth = (float)Px(GlyphSize, scale);
                graphics.DrawString(GlyphFor(item.Kind), fonts.Icon, brush,
                    new RectangleF(x, 0, glyphWidth, height), format);
                x += glyphWidth + (float)Px(GlyphToTextGap, scale);

                var text = FormatPercentage(item.Percentage);
                var textWidth = MeasureText(measureGraphics, text, fonts.Text);
                graphics.DrawString(text, fonts.Text, brush,
                    new RectangleF(x, 0, textWidth + 4, height), format);
                x += textWidth;

                if (ShowsBolt(item))
                {
                    x += (float)Px(TextToBoltGap, scale);
                    var boltWidth = (float)Px(BoltSize, scale);
                    graphics.DrawString(LightningBoltGlyph, fonts.Bolt, boltBrush,
                        new RectangleF(x, 0, boltWidth, height), format);
                    x += boltWidth;
                }

                x += (float)Px(ItemGap, scale);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    internal static string FormatPercentage(int? percentage) => percentage is { } value ? $"{value}%" : OfflineText;

    private static bool ShowsBolt(TaskbarStripItem item) => item.IsCharging && !item.IsOffline;

    internal static string GlyphFor(DeviceKind kind) => kind switch
    {
        DeviceKind.Keyboard => KeyboardGlyph,
        DeviceKind.Headset => HeadphoneGlyph,
        _ => MouseGlyph
    };

    private static int MeasureWidth(
        IReadOnlyList<TaskbarStripItem> items,
        double scale,
        Graphics graphics,
        StripFonts fonts)
    {
        double width = Px(EdgePadding, scale) * 2;
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            width += Px(GlyphSize, scale) + Px(GlyphToTextGap, scale);
            width += MeasureText(graphics, FormatPercentage(item.Percentage), fonts.Text);
            if (ShowsBolt(item))
            {
                width += Px(TextToBoltGap, scale) + Px(BoltSize, scale);
            }

            if (index < items.Count - 1)
            {
                width += Px(ItemGap, scale);
            }
        }

        return (int)Math.Ceiling(width);
    }

    private static float MeasureText(Graphics graphics, string text, Font font)
    {
        using var format = CreateFormat();
        return (float)Math.Ceiling(graphics.MeasureString(text, font, PointF.Empty, format).Width);
    }

    private static double Px(double value, double scale) => Math.Round(value * scale);

    private static void ConfigureText(Graphics graphics)
    {
        // ClearType cannot be used on pixels that are partly transparent.
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    private static StringFormat CreateFormat()
    {
        var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center
        };
        format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
        return format;
    }

    private static string ResolveFamily(string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (InstalledFamilies.Value.Contains(candidate))
            {
                return candidate;
            }
        }

        return candidates[^1];
    }

    private sealed class StripFonts : IDisposable
    {
        private StripFonts(Font text, Font icon, Font bolt)
        {
            Text = text;
            Icon = icon;
            Bolt = bolt;
        }

        public Font Text { get; }

        public Font Icon { get; }

        public Font Bolt { get; }

        public static StripFonts Create(double scale)
        {
            var iconFamily = ResolveFamily(IconFontCandidates);
            var textFamily = ResolveFamily(TextFontCandidates);
            return new StripFonts(
                new Font(textFamily, (float)(TextSize * scale), FontStyle.Regular, GraphicsUnit.Pixel),
                new Font(iconFamily, (float)(GlyphSize * scale), FontStyle.Regular, GraphicsUnit.Pixel),
                new Font(iconFamily, (float)(BoltSize * scale), FontStyle.Regular, GraphicsUnit.Pixel));
        }

        public void Dispose()
        {
            Text.Dispose();
            Icon.Dispose();
            Bolt.Dispose();
        }
    }
}

namespace Batterio.Core;

/// <summary>Helpers for Windows HID device interface paths.</summary>
public static class HidCollectionPaths
{
    private static readonly System.Text.RegularExpressions.Regex CollectionPart = new(
        @"&col[0-9a-f]{2}#(?<instance>[^#]*)&[0-9a-f]{4}#",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns a key shared by all top-level collections of one HID interface, so that sibling collections
    /// (for example <c>mi_02&amp;col01#7&amp;1edd123a&amp;0&amp;0000</c> and <c>mi_02&amp;col02#7&amp;1edd123a&amp;0&amp;0001</c>) can be paired.
    /// </summary>
    public static string InterfaceKey(string devicePath) =>
        CollectionPart.Replace(devicePath, match => $"#{match.Groups["instance"].Value}#", 1);

    // A Bluetooth LE HID path ends its device ID with the 48-bit address:
    // ...{00001812-...}_dev_vid&02046d_pid&b034_rev&0006_d06e9b62da62&col02#9&149d8fc1&0&0001#{...}
    private static readonly System.Text.RegularExpressions.Regex BluetoothAddressPart = new(
        @"_dev_vid&[0-9a-f]{6}_pid&[0-9a-f]{4}_rev&[0-9a-f]{4}_(?<address>[0-9a-f]{12})(?:&|#)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Extracts the Bluetooth device address from the path of a Bluetooth LE HID collection.</summary>
    public static bool TryParseBluetoothAddress(string devicePath, out ulong address)
    {
        address = 0;
        var match = BluetoothAddressPart.Match(devicePath);
        return match.Success &&
               ulong.TryParse(match.Groups["address"].Value, System.Globalization.NumberStyles.HexNumber, null, out address);
    }
}

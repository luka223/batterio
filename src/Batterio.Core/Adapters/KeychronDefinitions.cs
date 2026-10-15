using Batterio.Core.Abstractions;

namespace Batterio.Core;

public static class KeychronDefinitions
{
    public const ushort VendorId = 0x3434;

    /// <summary>Keychron Link 2.4 GHz dongle.</summary>
    public const ushort LinkProductId = 0xD030;

    /// <summary>First payload byte of the battery report pushed by the custom firmware.</summary>
    public const byte BatteryReportCommand = 0xA4;

    /// <summary>Model id reported by the custom firmware for the Q1 HE (the K10 HE uses 1).</summary>
    public const byte Q1HeModelId = 0x02;

    /// <summary>
    /// Raw HID collection of the Link dongle (interface 3, usage page 0xFF60, usage 0x61, 33-byte reports including
    /// report ID 0), measured on a real dongle. No required report ID: the collection uses report ID 0.
    /// </summary>
    public static readonly HidCollectionQuery LinkRawHidQuery = new()
    {
        VendorId = VendorId,
        ProductId = LinkProductId,
        InterfaceNumber = 3,
        UsagePage = 0xFF60,
        Usage = 0x0061,
        MinimumReportLength = 33
    };
}

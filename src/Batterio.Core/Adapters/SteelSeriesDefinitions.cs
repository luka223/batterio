using Batterio.Core.Abstractions;

namespace Batterio.Core;

public static class SteelSeriesDefinitions
{
    public const ushort VendorId = 0x1038;

    /// <summary>Arctis Nova Pro Omni hub in USB-1 mode. USB-2 (0x2292) and XBOX (0x2293) modes stay silent.</summary>
    public const ushort NovaProOmniProductId = 0x2290;

    /// <summary>Vendor status collection (interface 3, report ID 1, 64-byte reports) measured on a real hub.</summary>
    public static readonly HidCollectionQuery NovaProOmniQuery = new()
    {
        VendorId = VendorId,
        ProductId = NovaProOmniProductId,
        InterfaceNumber = 3,
        UsagePage = 0xFFC0,
        Usage = 0x0001,
        RequiredReportId = 0x01,
        MinimumReportLength = 64
    };
}

using Batterio.Core.Abstractions;

namespace Batterio.Core;

public sealed record BuiltInDeviceDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string AdapterId { get; init; }

    public required HidCollectionQuery ReceiverQuery { get; init; }

    public DeviceConfiguration CreateConfiguration() => new()
    {
        Id = Id,
        Name = Name,
        AdapterId = AdapterId,
        Enabled = true
    };
}

public static class LogitechReceiverDefinitions
{
    public static readonly HidCollectionQuery SuperstrikeQuery = new()
    {
        VendorId = LogitechGProX2SuperstrikeAdapter.VendorId,
        ProductId = LogitechGProX2SuperstrikeAdapter.ProductId,
        InterfaceNumber = LogitechGProX2SuperstrikeAdapter.InterfaceNumber,
        UsagePage = 0xFF00,
        MinimumReportLength = 20,
        RequiredReportId = 0x11
    };

    public static readonly HidCollectionQuery LightspeedQuery = new()
    {
        VendorId = LogitechGProX2LightspeedAdapter.VendorId,
        ProductId = LogitechGProX2LightspeedAdapter.ProductId,
        InterfaceNumber = LogitechGProX2LightspeedAdapter.InterfaceNumber,
        UsagePage = LogitechGProX2LightspeedAdapter.UsagePage,
        Usage = LogitechGProX2LightspeedAdapter.Usage,
        MinimumReportLength = 64,
        RequiredReportId = 0x51
    };
}

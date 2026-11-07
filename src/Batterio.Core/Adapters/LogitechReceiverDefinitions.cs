using Batterio.Core.Abstractions;

namespace Batterio.Core;

public sealed record BuiltInDeviceDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string AdapterId { get; init; }

    public required DeviceKind Kind { get; init; }

    public required IReadOnlyList<HidCollectionQuery> PresenceQueries { get; init; }

    /// <summary>The device reports a second (reserve) battery that is shown beneath the primary one.</summary>
    public bool HasReserveBattery { get; init; }

    public DeviceConfiguration CreateConfiguration() => new()
    {
        Id = Id,
        Name = Name,
        AdapterId = AdapterId,
        Kind = Kind,
        Enabled = true,
        HasReserveBattery = HasReserveBattery
    };
}

public static class LogitechReceiverDefinitions
{
    public static readonly HidCollectionQuery SuperstrikeReceiverQuery = new()
    {
        VendorId = LogitechGProX2SuperstrikeAdapter.VendorId,
        ProductId = LogitechGProX2SuperstrikeAdapter.ReceiverProductId,
        InterfaceNumber = LogitechGProX2SuperstrikeAdapter.InterfaceNumber,
        UsagePage = 0xFF00,
        MinimumReportLength = 20,
        RequiredReportId = 0x11
    };

    public static readonly HidCollectionQuery SuperstrikeWiredQuery = new()
    {
        VendorId = LogitechGProX2SuperstrikeAdapter.VendorId,
        ProductId = LogitechGProX2SuperstrikeAdapter.WiredProductId,
        InterfaceNumber = LogitechGProX2SuperstrikeAdapter.InterfaceNumber,
        UsagePage = 0xFF00,
        Usage = 0x0002,
        MinimumReportLength = 20,
        RequiredReportId = 0x11
    };

    public static readonly HidCollectionQuery BoltReceiverQuery = new()
    {
        VendorId = LogitechBoltAdapter.VendorId,
        ProductId = LogitechBoltAdapter.ReceiverProductId,
        InterfaceNumber = LogitechBoltAdapter.InterfaceNumber,
        UsagePage = 0xFF00,
        MinimumReportLength = 20,
        RequiredReportId = 0x11
    };

    /// <summary>The Bolt receiver's short-report (0x10) HID++ collection, where it sends HID++ 1.0 error replies.</summary>
    public static readonly HidCollectionQuery BoltReceiverShortQuery = new()
    {
        VendorId = LogitechBoltAdapter.VendorId,
        ProductId = LogitechBoltAdapter.ReceiverProductId,
        InterfaceNumber = LogitechBoltAdapter.InterfaceNumber,
        UsagePage = 0xFF00,
        MinimumReportLength = 7,
        RequiredReportId = 0x10
    };

    /// <summary>
    /// The HID++ collection of a Logitech device connected directly over Bluetooth LE
    /// (shown by Windows as "Logitech Download Assistant"): vendor usage page 0xFF43, usage 0x0202, long reports only.
    /// </summary>
    public static HidCollectionQuery BluetoothHidppQuery(ushort productId) => new()
    {
        VendorId = LogitechBoltAdapter.VendorId,
        ProductId = productId,
        UsagePage = 0xFF43,
        Usage = 0x0202,
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

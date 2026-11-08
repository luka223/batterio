using Batterio.Core.Abstractions;

namespace Batterio.Core;

public sealed record BuiltInDeviceDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string AdapterId { get; init; }

    public required DeviceKind Kind { get; init; }

    /// <summary>The transports through which the device can be reached; its presence is the presence of any resolved route.</summary>
    public required IReadOnlyList<DeviceRouteDescriptor> Routes { get; init; }

    /// <summary>The HID collections whose existence alone proves the device is present (its <see cref="HidPresenceRoute"/>s).</summary>
    public IReadOnlyList<HidCollectionQuery> PresenceQueries =>
        Routes.OfType<HidPresenceRoute>().Select(route => route.Query).ToArray();

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

    public const ushort LogitechVendorId = 0x046D;

    /// <summary>Logi Bolt receiver, HID++ on USB interface 2 (Solaar <c>base_usb.py</c>, <c>_bolt_receiver</c>).</summary>
    public const ushort BoltReceiverProductId = 0xC548;

    public const ushort BoltReceiverInterfaceNumber = 2;

    /// <summary>The Bolt receiver's long-report (0x11) HID++ collection.</summary>
    public static readonly HidCollectionQuery BoltReceiverQuery = new()
    {
        VendorId = LogitechVendorId,
        ProductId = BoltReceiverProductId,
        InterfaceNumber = BoltReceiverInterfaceNumber,
        UsagePage = 0xFF00,
        MinimumReportLength = 20,
        RequiredReportId = 0x11
    };

    /// <summary>The Bolt receiver's short-report (0x10) HID++ collection, where it sends HID++ 1.0 error replies.</summary>
    public static readonly HidCollectionQuery BoltReceiverShortQuery = new()
    {
        VendorId = LogitechVendorId,
        ProductId = BoltReceiverProductId,
        InterfaceNumber = BoltReceiverInterfaceNumber,
        UsagePage = 0xFF00,
        MinimumReportLength = 7,
        RequiredReportId = 0x10
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

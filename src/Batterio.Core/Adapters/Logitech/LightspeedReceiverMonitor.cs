using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Follows the G Pro X2 Superstrike going to sleep and waking up, using only what its Lightspeed receiver announces by itself: it keeps
/// the receiver's short-report collection open (listen-only) and reads the HID++ 1.0 device connection notices (<c>0x41</c>).
/// The receiver's route is <see cref="RouteState.Online"/> after a notice that the link was established and
/// <see cref="RouteState.Offline"/> after one that it was lost, so a mouse that is moved after sleeping is read right away.
/// </summary>
/// <remarks>
/// Hardware findings (2026-11-09, no G HUB installed, nothing written to the receiver): the notices arrive on the short collection
/// (interface 2, usage page 0xFF00, usage 0x0001, report 0x10) without any request or setting. About 5 minutes after the mouse was last
/// used the receiver sent <c>10 01 41 11 62 BD 40</c> (link lost); when the mouse was moved it sent <c>10 01 41 11 A2 BD 40</c> (link
/// established) followed by six <c>11 01 …</c> reports from the mouse. Layout, as in Solaar: <c>10 &lt;slot&gt; 41 &lt;protocol&gt;
/// &lt;flags&gt; &lt;wpid low&gt; &lt;wpid high&gt;</c>; bit 0x40 of the flags is set while the link is down, and the wireless product ID is
/// <c>0x40BD</c> for the Superstrike. Notices about other devices paired to the same receiver are ignored.
/// A link-lost notice with no cable plugged in leaves only Offline routes, so the mouse is shown as Offline about 3 s later
/// (<see cref="DeviceSelectionCoordinator.DefaultOfflineGrace"/>) without any request; on its cable it stays reachable and is not marked.
/// If a read succeeds meanwhile (for example right after the cable is unplugged), the mark is skipped.
/// </remarks>
public sealed class LightspeedReceiverMonitor(IHidCollectionProvider provider, IBatterioLog? log = null) : UsbEventStateMonitor(provider, log)
{
    public const byte ShortReportId = 0x10;
    public const byte DeviceConnectionNotice = 0x41;
    public const byte LinkDownFlag = 0x40;

    /// <summary>The receiver's short-report (0x10) HID++ collection, where it sends its connection notices.</summary>
    public static readonly HidCollectionQuery NoticeQuery = LogitechGProX2Superstrike.ReceiverQuery with
    {
        MinimumReportLength = 7,
        RequiredReportId = ShortReportId
    };

    protected override HidCollectionQuery EventQuery => NoticeQuery;

    protected override HidCollectionQuery RouteQuery => LogitechGProX2Superstrike.ReceiverQuery;

    protected override string Description => "Superstrike receiver connection notices";

    protected override RouteState? Decode(ReadOnlySpan<byte> frame) =>
        TryDecodeNotice(frame, LogitechGProX2Superstrike.ExpectedWirelessModelId, out var linkEstablished)
            ? linkEstablished ? RouteState.Online : RouteState.Offline
            : null;

    /// <summary>Decodes a device connection notice about the device with the wireless product ID <paramref name="wirelessProductId"/>.</summary>
    public static bool TryDecodeNotice(ReadOnlySpan<byte> frame, ushort wirelessProductId, out bool linkEstablished)
    {
        linkEstablished = false;
        if (frame.Length < 7 || frame[0] != ShortReportId || frame[2] != DeviceConnectionNotice)
        {
            return false;
        }

        if ((ushort)(frame[5] | (frame[6] << 8)) != wirelessProductId)
        {
            return false;
        }

        linkEstablished = (frame[4] & LinkDownFlag) == 0;
        return true;
    }
}

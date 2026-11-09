using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Follows the Arctis Nova Pro Omni headset connecting to and disconnecting from its hub, using only what the hub announces by itself:
/// it keeps the hub's event collection open (listen-only, nothing is ever written) and reads the <c>07 B5</c> connection events.
/// The hub's route is <see cref="RouteState.Online"/> after a connect event and <see cref="RouteState.Offline"/> after a disconnect event,
/// so a headset that is switched on is read right away. Before the first event nothing is known and the route stays
/// <see cref="RouteState.Unknown"/>.
/// </summary>
/// <remarks>
/// Hardware findings (2026-11-09, two off/on cycles): the events arrive on interface 3, usage page 0xFF00 (the status collection on usage
/// page 0xFFC0 stays silent). Switching the headset off sends <c>07 B5 01 00 04</c> and <c>07 B7 00 64 01</c> at once, then
/// <c>07 43</c>, <c>07 45</c> and <c>07 47</c> about 2.7 s later. Switching it on sends <c>07 B7 6x 64 08</c>, <c>07 80 03 0A 01 …</c>,
/// <c>07 B5 01 00 08</c> and <c>07 19</c>, about 5 s after the button press (boot plus connecting to GameHub).
/// Only the <c>B5</c> frame is used.
/// </remarks>
public sealed class SteelSeriesHubMonitor(IHidCollectionProvider provider, IBatterioLog? log = null) : UsbEventStateMonitor(provider, log)
{
    protected override HidCollectionQuery EventQuery => SteelSeriesArctisNovaProOmni.EventsQuery;

    protected override HidCollectionQuery RouteQuery => SteelSeriesArctisNovaProOmni.NovaProOmniQuery;

    protected override string Description => "Arctis Nova Pro Omni connection events";

    protected override RouteState? Decode(ReadOnlySpan<byte> frame) =>
        SteelSeriesNovaProOmniStatusDecoding.TryDecodeConnectionEvent(frame, out var connected)
            ? connected ? RouteState.Online : RouteState.Offline
            : null;
}

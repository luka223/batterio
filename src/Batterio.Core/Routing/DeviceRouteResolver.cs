using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Turns a catalog device's route descriptors into the <see cref="ResolvedRoute"/>s that currently exist, given the HID collections that
/// were found. It owns no state of its own: the Logi Bolt receivers, the Bluetooth monitor and the USB state sources report what they know.
/// Discovery calls it on every scan; tests and hardware smoke tests call it directly.
/// </summary>
public sealed class DeviceRouteResolver
{
    private readonly LogiBoltReceiverManager? _bolt;
    private readonly IBluetoothConnectionMonitor _bluetooth;
    private readonly IReadOnlyList<IUsbRouteStateSource> _usbSources;

    public DeviceRouteResolver(
        LogiBoltReceiverManager? bolt = null,
        IBluetoothConnectionMonitor? bluetooth = null,
        IEnumerable<IUsbRouteStateSource>? usbSources = null)
    {
        _bolt = bolt;
        _bluetooth = bluetooth ?? new NullBluetoothConnectionMonitor();
        _usbSources = usbSources?.ToArray() ?? [];
    }

    /// <summary>
    /// Resolves the routes of <paramref name="definition"/>. <paramref name="failures"/> holds the queries whose enumeration failed;
    /// the failure of the first query that found nothing is returned, so that a device with no route at all is not reported as absent when
    /// the reason may be that it could not be looked for (discovery only looks at it when no route was resolved).
    /// </summary>
    public (IReadOnlyList<ResolvedRoute> Routes, Exception? Failure) Resolve(
        BuiltInDeviceDefinition definition,
        IReadOnlyList<HidCollectionInfo> collections,
        IReadOnlyDictionary<HidCollectionQuery, Exception> failures)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(collections);
        ArgumentNullException.ThrowIfNull(failures);

        var routes = new List<ResolvedRoute>();
        Exception? failure = null;
        foreach (var descriptor in definition.Routes)
        {
            switch (descriptor)
            {
                case UsbHidRouteDescriptor usb:
                    var usbEndpoints = collections.Where(usb.Query.Matches)
                        .OrderBy(collection => collection.DevicePath, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    foreach (var endpoint in usbEndpoints)
                    {
                        routes.Add(new ResolvedRoute(definition.Id, usb.Kind, endpoint.DevicePath, null, UsbState(usb.Kind, endpoint), endpoint));
                    }

                    if (usbEndpoints.Length == 0 && failures.TryGetValue(usb.Query, out var usbFailure)) failure ??= usbFailure;
                    break;

                case HidppBluetoothRouteDescriptor bluetooth:
                    var bluetoothQuery = HidppBluetoothRoute.Query(bluetooth.ProductId);
                    var bluetoothEndpoints = collections.Where(bluetoothQuery.Matches)
                        .OrderBy(collection => collection.DevicePath, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    foreach (var endpoint in bluetoothEndpoints)
                    {
                        routes.Add(new ResolvedRoute(definition.Id, DeviceRouteKind.HidppBluetooth, endpoint.DevicePath, null,
                            _bluetooth.GetState(endpoint.DevicePath) switch
                            {
                                BluetoothLinkState.Connected => RouteState.Online,
                                BluetoothLinkState.Disconnected => RouteState.Offline,
                                _ => RouteState.Unknown
                            }, endpoint));
                    }

                    if (bluetoothEndpoints.Length == 0 && failures.TryGetValue(bluetoothQuery, out var bluetoothFailure)) failure ??= bluetoothFailure;
                    break;

                case LogiBoltRouteDescriptor bolt when _bolt is not null:
                    if (failures.TryGetValue(LogiBoltReceiverManager.BoltReceiverQuery, out var boltFailure))
                    {
                        failure ??= boltFailure;
                        break;
                    }

                    foreach (var match in _bolt.FindAll(bolt.WirelessProductId, bolt.Names))
                    {
                        routes.Add(new ResolvedRoute(definition.Id, DeviceRouteKind.LogiBolt, match.Receiver.Path, match.Slot.Slot,
                            match.Slot.Online ? RouteState.Online : RouteState.Offline));
                    }

                    break;
            }
        }

        return (routes, failure);
    }

    /// <summary>The queries discovery must enumerate to resolve <paramref name="descriptor"/>.</summary>
    public static IEnumerable<HidCollectionQuery> QueriesOf(DeviceRouteDescriptor descriptor) => descriptor switch
    {
        UsbHidRouteDescriptor usb => [usb.Query],
        HidppBluetoothRouteDescriptor bluetooth => [HidppBluetoothRoute.Query(bluetooth.ProductId)],
        LogiBoltRouteDescriptor => [LogiBoltReceiverManager.BoltReceiverQuery, LogiBoltReceiverManager.BoltReceiverShortQuery],
        _ => []
    };

    private RouteState UsbState(DeviceRouteKind kind, HidCollectionInfo endpoint)
    {
        // A device on a cable is powered and enumerated, so it is awake.
        if (kind == DeviceRouteKind.UsbCable)
        {
            return RouteState.Online;
        }

        foreach (var source in _usbSources)
        {
            if (source.GetState(endpoint) is RouteState state)
            {
                return state;
            }
        }

        return RouteState.Unknown;
    }
}

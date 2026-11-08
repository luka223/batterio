using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Expected identity of a Logitech HID++ 2.0 device.
/// </summary>
/// <remarks>
/// Sources and hardware confirmation:
/// <list type="bullet">
/// <item>MX Master 3S reports product ID 0xB034 and MX Mechanical 0xB366, confirmed on hardware over Bluetooth LE: Windows lists
/// them as <c>VID&amp;02046D_PID&amp;B034</c> / <c>PID&amp;B366</c>, and their Device Name (0x0005) is "MX Master 3S" / "MX Mechanical".</item>
/// <item>Device Information (0x0003) layout (transport bitmap at byte 6, model IDs from byte 7 in the order BT, BLE, eQuad, USB),
/// Device Name (0x0005), Unified Battery (0x1004) and Battery Status (0x1000): Solaar <c>hidpp20.py</c>.</item>
/// </list>
/// A device matches when any of its model IDs is expected, or its reported name is expected.
/// </remarks>
public sealed record LogitechHidppDeviceProfile(
    string AdapterId,
    string DisplayName,
    IReadOnlySet<ushort> ExpectedModelIds,
    IReadOnlySet<string> ExpectedNames)
{
    public bool Matches(string? name, IEnumerable<ushort> modelIds) =>
        modelIds.Any(ExpectedModelIds.Contains) ||
        (!string.IsNullOrWhiteSpace(name) && ExpectedNames.Contains(name.Trim()));

    public bool Matches(HidppDeviceIdentity identity) => Matches(identity.Name, identity.ModelIds);
}

/// <summary>What a HID++ 2.0 device reports about itself: its name and the model IDs of its transports.</summary>
public sealed record HidppDeviceIdentity(string? Name, IReadOnlyList<ushort> ModelIds, string ModelText)
{
    private const ushort DeviceInformationFeature = 0x0003;

    /// <summary>Pings the device, then reads Device Information (0x0003) and Device Name (0x0005).</summary>
    /// <exception cref="BatteryAdapterException"><see cref="BatteryAdapterErrorKind.DeviceOffline"/> when the ping is not answered in time.</exception>
    public static async Task<HidppDeviceIdentity> ReadAsync(
        HidppClient client,
        byte deviceIndex,
        TimeSpan pingTimeout,
        string source,
        CancellationToken cancellationToken)
    {
        using (var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            probe.CancelAfter(pingTimeout);
            try
            {
                await client.PingAsync(deviceIndex, probe.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline, $"{source} did not answer its probe.");
            }
        }

        IReadOnlyList<ushort> modelIds = Array.Empty<ushort>();
        var modelText = "unavailable";
        try
        {
            var infoIndex = await client.ResolveFeatureAsync(deviceIndex, DeviceInformationFeature, cancellationToken).ConfigureAwait(false);
            var info = await client.SendAsync(deviceIndex, infoIndex, 0, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            modelIds = LogitechDeviceInformation.GetModelIds(info.Parameters).Select(entry => entry.ModelId).ToArray();
            modelText = LogitechDeviceInformation.FormatModelIds(info.Parameters);
        }
        catch (BatteryAdapterException exception) when (exception.Kind == BatteryAdapterErrorKind.UnsupportedCapability)
        {
        }

        var name = await LogitechDeviceName.ReadAsync(client, deviceIndex, cancellationToken).ConfigureAwait(false);
        return new HidppDeviceIdentity(name, modelIds, modelText);
    }
}

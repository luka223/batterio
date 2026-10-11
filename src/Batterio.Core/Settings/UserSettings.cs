using System.Text.Json.Serialization;

namespace Batterio.Core;

public sealed class UserSettings
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; init; } = CurrentVersion;

    [JsonPropertyName("deviceSelections")]
    public Dictionary<string, bool> DeviceSelections { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    // Devices that have been connected to this PC at least once. Older settings
    // files omit this property. A stored selection does not imply the device is
    // known, because earlier versions listed every supported device for toggling.
    [JsonPropertyName("knownDevices")]
    public HashSet<string> KnownDevices { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    public static UserSettings Defaults() => new();

    public bool IsDeviceSelected(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return !DeviceSelections.TryGetValue(deviceId, out var selected) || selected;
    }

    public bool IsDeviceKnown(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return KnownDevices.Contains(deviceId);
    }

    public UserSettings WithDeviceSelection(string deviceId, bool selected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var selections = new Dictionary<string, bool>(DeviceSelections, StringComparer.OrdinalIgnoreCase)
        {
            [deviceId] = selected
        };
        return new UserSettings
        {
            Version = Version,
            DeviceSelections = selections,
            KnownDevices = new HashSet<string>(KnownDevices, StringComparer.OrdinalIgnoreCase)
        };
    }

    public UserSettings WithKnownDevices(IEnumerable<string> deviceIds)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        var known = new HashSet<string>(KnownDevices, StringComparer.OrdinalIgnoreCase);
        var selections = new Dictionary<string, bool>(DeviceSelections, StringComparer.OrdinalIgnoreCase);
        foreach (var deviceId in deviceIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()))
        {
            // A selection stored before the device was ever connected is stale; a
            // newly connected device starts visible.
            if (known.Add(deviceId))
            {
                selections.Remove(deviceId);
            }
        }

        return new UserSettings
        {
            Version = Version,
            DeviceSelections = selections,
            KnownDevices = known
        };
    }

    public UserSettings Normalize()
    {
        var selections = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in DeviceSelections ?? [])
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                selections[pair.Key.Trim()] = pair.Value;
            }
        }

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var deviceId in KnownDevices ?? [])
        {
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                known.Add(deviceId.Trim());
            }
        }

        return new UserSettings
        {
            Version = Version,
            DeviceSelections = selections,
            KnownDevices = known
        };
    }
}

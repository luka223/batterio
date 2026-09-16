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

    public static UserSettings Defaults() => new();

    public bool IsDeviceSelected(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return !DeviceSelections.TryGetValue(deviceId, out var selected) || selected;
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
            DeviceSelections = selections
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

        return new UserSettings
        {
            Version = Version,
            DeviceSelections = selections
        };
    }
}

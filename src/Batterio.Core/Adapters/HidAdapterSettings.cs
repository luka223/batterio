using System.Text.Json;

namespace Batterio.Core;

internal static class HidAdapterSettings
{
    public static string? ReadDevicePath(JsonElement settings, string adapterId)
    {
        if (settings.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        if (settings.ValueKind != JsonValueKind.Object)
        {
            throw new AdapterConfigurationException($"Adapter '{adapterId}' settings must be a JSON object.");
        }

        if (!settings.TryGetProperty("devicePath", out var path))
        {
            return null;
        }

        if (path.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(path.GetString()))
        {
            throw new AdapterConfigurationException($"Adapter '{adapterId}' devicePath must be a nonempty string when supplied.");
        }

        return path.GetString();
    }
}

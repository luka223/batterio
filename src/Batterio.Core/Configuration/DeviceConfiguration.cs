using System.Text.Json;
using System.Text.Json.Serialization;

namespace Batterio.Core;

public sealed class DeviceConfiguration
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("photoPath")]
    public string? PhotoPath { get; init; }

    [JsonPropertyName("adapterId")]
    public string AdapterId { get; init; } = string.Empty;

    [JsonPropertyName("adapterSettings")]
    public JsonElement AdapterSettings { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("pollIntervalSeconds")]
    public int PollIntervalSeconds { get; init; } = 300;

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; init; } = 10;

    public string? ResolvePhotoPath(string configurationDirectory)
    {
        if (string.IsNullOrWhiteSpace(PhotoPath))
        {
            return null;
        }

        return Path.GetFullPath(Path.IsPathFullyQualified(PhotoPath)
            ? PhotoPath
            : Path.Combine(configurationDirectory, PhotoPath));
    }
}

using System.Text.Json.Serialization;

namespace Batterio.Core;

public sealed class ConfigurationDocument
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = ConfigurationLoader.CurrentVersion;

    [JsonPropertyName("devices")]
    public List<DeviceConfiguration>? Devices { get; init; }
}

public sealed record ConfigurationSnapshot(
    string FilePath,
    string DirectoryPath,
    IReadOnlyList<DeviceConfiguration> Devices);

public sealed class ConfigurationValidationException : Exception
{
    public ConfigurationValidationException(IEnumerable<string> errors)
        : base("The device configuration is invalid: " + string.Join("; ", errors))
    {
        Errors = errors.ToArray();
    }

    public IReadOnlyList<string> Errors { get; }
}

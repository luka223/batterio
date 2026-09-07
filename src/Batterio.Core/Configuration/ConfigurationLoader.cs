using System.Text.Json;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

public sealed class ConfigurationLoader
{
    public const int CurrentVersion = 1;
    public const int DefaultPollIntervalSeconds = 300;
    public const int DefaultTimeoutSeconds = 10;
    public const int MinimumPollIntervalSeconds = 5;
    public const int MinimumTimeoutSeconds = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        WriteIndented = true
    };

    private readonly IBatterioLog _log;

    public ConfigurationLoader(string filePath, IBatterioLog? log = null)
    {
        FilePath = Path.GetFullPath(filePath);
        _log = log ?? new NullBatterioLog();
    }

    public string FilePath { get; }

    public string DirectoryPath => Path.GetDirectoryName(FilePath)!;

    public async Task<ConfigurationSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DirectoryPath);

        if (!File.Exists(FilePath))
        {
            await WriteDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Log(BatterioLogLevel.Error, $"Could not read configuration file '{FilePath}'.", exception);
            throw;
        }

        ConfigurationDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ConfigurationDocument>(json, SerializerOptions);
        }
        catch (JsonException exception)
        {
            _log.Log(BatterioLogLevel.Error, "Configuration JSON is malformed.", exception);
            throw new ConfigurationValidationException(new[] { "The document is not valid JSON." });
        }

        if (document is null)
        {
            throw new ConfigurationValidationException(new[] { "The document is empty." });
        }

        var devices = document.Devices ?? new List<DeviceConfiguration>();
        var errors = Validate(document.Version, devices);
        if (errors.Count > 0)
        {
            _log.Log(BatterioLogLevel.Error, $"Configuration rejected: {string.Join("; ", errors)}");
            throw new ConfigurationValidationException(errors);
        }

        return new ConfigurationSnapshot(FilePath, DirectoryPath, devices.AsReadOnly());
    }

    public static IReadOnlyList<string> Validate(int version, IReadOnlyList<DeviceConfiguration> devices)
    {
        var errors = new List<string>();
        if (version != CurrentVersion)
        {
            errors.Add($"Unsupported configuration version {version}; expected {CurrentVersion}.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < devices.Count; index++)
        {
            var device = devices[index];
            var label = $"Device {index + 1}";
            if (string.IsNullOrWhiteSpace(device.Id))
            {
                errors.Add($"{label} has a missing id.");
            }
            else if (!ids.Add(device.Id.Trim()))
            {
                errors.Add($"Device id '{device.Id}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(device.Name))
            {
                errors.Add($"{label} has a missing name.");
            }

            if (string.IsNullOrWhiteSpace(device.AdapterId))
            {
                errors.Add($"{label} has a missing adapterId.");
            }

            if (device.AdapterSettings.ValueKind is not JsonValueKind.Object)
            {
                errors.Add($"Device '{device.Id}' adapterSettings must be a JSON object.");
            }

            if (device.PollIntervalSeconds < MinimumPollIntervalSeconds)
            {
                errors.Add($"Device '{device.Id}' pollIntervalSeconds must be at least {MinimumPollIntervalSeconds}.");
            }

            if (device.TimeoutSeconds < MinimumTimeoutSeconds)
            {
                errors.Add($"Device '{device.Id}' timeoutSeconds must be at least {MinimumTimeoutSeconds}.");
            }
            else if (device.TimeoutSeconds > device.PollIntervalSeconds)
            {
                errors.Add($"Device '{device.Id}' timeoutSeconds cannot exceed pollIntervalSeconds.");
            }
        }

        return errors;
    }

    private async Task WriteDefaultAsync(CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(new ConfigurationDocument
        {
            Version = CurrentVersion,
            Devices = new List<DeviceConfiguration>()
        }, SerializerOptions);
        await File.WriteAllTextAsync(FilePath, json, cancellationToken).ConfigureAwait(false);
        _log.Log(BatterioLogLevel.Information, $"Created default configuration at '{FilePath}'.");
    }
}

public sealed class ConfigurationPathProvider
{
    public string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Batterio");

    public string FilePath => Path.Combine(DirectoryPath, "devices.json");

    public string LogsDirectoryPath => Path.Combine(DirectoryPath, "Logs");
}

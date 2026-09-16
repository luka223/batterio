using System.Text.Json;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

public sealed class UserSettingsStoreException : IOException
{
    public UserSettingsStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class JsonUserSettingsStore : IUserSettingsStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        WriteIndented = true
    };

    private readonly IBatterioLog _log;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _disposed;

    public JsonUserSettingsStore(string filePath, IBatterioLog? log = null)
    {
        FilePath = Path.GetFullPath(filePath);
        BackupFilePath = FilePath + ".bak";
        _log = log ?? new NullBatterioLog();
    }

    public string FilePath { get; }

    public string BackupFilePath { get; }

    public async Task<UserSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!File.Exists(FilePath))
        {
            return UserSettings.Defaults();
        }

        try
        {
            var json = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !HasProperty(document.RootElement, "version") ||
                !HasProperty(document.RootElement, "deviceSelections"))
            {
                throw new JsonException("User settings must contain version and deviceSelections.");
            }

            var settings = document.RootElement.Deserialize<UserSettings>(SerializerOptions);
            if (settings is null || settings.Version != UserSettings.CurrentVersion || settings.DeviceSelections is null)
            {
                throw new JsonException($"Unsupported user settings version; expected {UserSettings.CurrentVersion}.");
            }

            return settings.Normalize();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            _log.Log(BatterioLogLevel.Error,
                $"User settings at '{FilePath}' could not be loaded; using defaults.", exception);
            return UserSettings.Defaults();
        }
    }

    public async Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (settings.Version != UserSettings.CurrentVersion)
        {
            throw new ArgumentException($"Unsupported user settings version {settings.Version}.", nameof(settings));
        }

        var normalized = settings.Normalize();
        var directory = Path.GetDirectoryName(FilePath) ?? throw new IOException("The settings path has no directory.");
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(normalized, SerializerOptions);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);

            if (File.Exists(FilePath))
            {
                // Keep the last committed document recoverable. Copying happens before
                // replacement, so a failure still leaves the destination untouched.
                File.Copy(FilePath, BackupFilePath, overwrite: true);
                File.Replace(temporaryPath, FilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                temporaryPath = null;
            }
            else
            {
                File.Move(temporaryPath, FilePath);
                temporaryPath = null;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.Log(BatterioLogLevel.Error, $"Could not save user settings to '{FilePath}'.", exception);
            throw new UserSettingsStoreException($"Could not save user settings to '{FilePath}'.", exception);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _log.Log(BatterioLogLevel.Warning,
                        $"Could not remove temporary user settings file '{temporaryPath}'.", exception);
                }
            }

            _writeGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _writeGate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static bool HasProperty(JsonElement element, string name) =>
        element.EnumerateObject().Any(property =>
            string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase));
}

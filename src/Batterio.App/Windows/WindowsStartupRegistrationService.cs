using Batterio.Core.Abstractions;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace Batterio.App.Windows;

public interface IStartupRegistry
{
    IStartupRegistryKey? OpenCurrentUser(string subKeyPath, bool writable);

    IStartupRegistryKey? OpenOrCreateCurrentUser(string subKeyPath, bool writable) =>
        OpenCurrentUser(subKeyPath, writable);
}

public interface IStartupRegistryKey : IDisposable
{
    object? GetValue(string name);

    RegistryValueKind? GetValueKind(string name);

    void SetValue(string name, object value, RegistryValueKind valueKind);

    void DeleteValue(string name, bool throwOnMissingValue);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsStartupRegistry : IStartupRegistry
{
    public IStartupRegistryKey? OpenCurrentUser(string subKeyPath, bool writable) =>
        Registry.CurrentUser.OpenSubKey(subKeyPath, writable) is { } key
            ? new WindowsStartupRegistryKey(key)
            : null;

    public IStartupRegistryKey? OpenOrCreateCurrentUser(string subKeyPath, bool writable) =>
        Registry.CurrentUser.CreateSubKey(subKeyPath, writable) is { } key
            ? new WindowsStartupRegistryKey(key)
            : null;

    private sealed class WindowsStartupRegistryKey(RegistryKey key) : IStartupRegistryKey
    {
        public object? GetValue(string name) => key.GetValue(name);

        public RegistryValueKind? GetValueKind(string name) => key.GetValueKind(name);

        public void SetValue(string name, object value, RegistryValueKind valueKind) => key.SetValue(name, value, valueKind);

        public void DeleteValue(string name, bool throwOnMissingValue) => key.DeleteValue(name, throwOnMissingValue);

        public void Dispose() => key.Dispose();
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsStartupRegistrationService : IStartupRegistrationService
{
    public const string AppName = "Batterio";
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const int MaximumRunCommandLength = 260;

    private readonly IStartupRegistry _registry;
    private readonly Func<string?>? _processPathProvider;
    private readonly Func<string, bool> _fileExists;
    private readonly bool _useInstalledResolver;
    private readonly Func<InstalledExecutablePaths>? _installedPathsProvider;
    public WindowsStartupRegistrationService(
        IStartupRegistry? registry = null,
        Func<string?>? processPathProvider = null,
        Func<string, bool>? fileExists = null,
        Func<InstalledExecutablePaths>? installedPathsProvider = null)
    {
        _registry = registry ?? new WindowsStartupRegistry();
        _processPathProvider = processPathProvider;
        _fileExists = fileExists ?? File.Exists;
        _useInstalledResolver = processPathProvider is null;
        _installedPathsProvider = installedPathsProvider;
    }

    public Task<bool> IsRegisteredAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var key = _registry.OpenCurrentUser(RunKeyPath, writable: false);
        if (key?.GetValue(AppName) is not string storedCommand ||
            GetValueKindOrMissing(key) != RegistryValueKind.String)
        {
            return Task.FromResult(false);
        }

        try
        {
            return Task.FromResult(string.Equals(storedCommand, ResolveStartupCommand(), StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult(false);
        }
    }

    public Task SetRegisteredAsync(bool registered, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (registered)
        {
            var command = ResolveStartupCommand();
            using var key = _registry.OpenOrCreateCurrentUser(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException($"Could not open the current-user startup key '{RunKeyPath}'.");
            key.SetValue(AppName, command, RegistryValueKind.String);
        }
        else
        {
            using var key = _registry.OpenCurrentUser(RunKeyPath, writable: true);
            key?.DeleteValue(AppName, throwOnMissingValue: false);
        }

        return Task.CompletedTask;
    }

    private static RegistryValueKind? GetValueKindOrMissing(IStartupRegistryKey key)
    {
        try
        {
            return key.GetValueKind(AppName);
        }
        // RegistryKey.GetValueKind puts the raw Win32 error in IOException.HResult.
        // The value can disappear between GetValue and GetValueKind. Only
        // ERROR_FILE_NOT_FOUND (2) means absent; other read failures must surface.
        catch (IOException exception) when (exception.HResult == 2)
        {
            return null;
        }
    }

    private string ResolveStartupCommand()
    {
        var path = _useInstalledResolver
            ? (_installedPathsProvider?.Invoke() ?? InstalledExecutableResolver.Resolve(fileExists: _fileExists)).LauncherPath
            : _processPathProvider!();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Windows startup registration requires a resolved executable path.");
        }

        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException("Windows startup registration requires a valid executable path.", exception);
        }

        var fileName = Path.GetFileName(path);
        if (fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("dotnet-host", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("dotnet-host.exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("apphost", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("apphost.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Windows startup registration cannot use a generic .NET host executable.");
        }

        if (!_fileExists(path))
        {
            throw new InvalidOperationException($"The startup executable '{path}' does not exist.");
        }

        var command = $"\"{path}\"";
        if (command.Length > MaximumRunCommandLength)
        {
            throw new InvalidOperationException(
                $"The startup command is {command.Length} characters long; Windows Run entries support at most {MaximumRunCommandLength} characters.");
        }

        return command;
    }
}

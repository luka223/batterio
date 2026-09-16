namespace Batterio.App.Windows;

/// <summary>Repairs persistent registration and owns the process's COM class factory.</summary>
public sealed class WindowsToastRegistrationService : IDisposable
{
    public const string AppUserModelId = "com.batterio.tray";
    public static readonly Guid ActivatorId = new("B68D18D7-6B67-4C24-993C-B597D789E895");
    public const string ActivationArgument = "-ToastActivated";
    private readonly IWindowsToastPlatform _platform;
    private readonly WindowsToastActivation _activation;
    private readonly Func<string?> _processPath;
    private readonly Func<string, bool> _fileExists;
    private readonly object _gate = new();
    private IDisposable? _activator;
    private bool _disposed;

    public WindowsToastRegistrationService(IWindowsToastPlatform platform, WindowsToastActivation activation,
        Func<string?>? processPath = null, Func<string, bool>? fileExists = null)
    {
        _platform = platform;
        _activation = activation;
        _processPath = processPath ?? (() => Environment.ProcessPath);
        _fileExists = fileExists ?? File.Exists;
    }

    public void RegisterCurrentExecutable()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activator is not null) return;
            var path = ResolveExecutablePath();
            _platform.SetProcessAppId(AppUserModelId);
            _platform.WriteShortcut(new(path, AppUserModelId, ActivatorId));
            _platform.WriteComServer(ActivatorId, path, $"\"{path}\" {ActivationArgument}");
            _activator = _platform.RegisterActivator(ActivatorId, (appId, _) =>
            {
                if (string.Equals(appId, AppUserModelId, StringComparison.Ordinal))
                    _activation.RequestPopup();
            });
        }
    }

    private string ResolveExecutablePath()
    {
        var path = _processPath();
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("Toast registration requires a resolved executable path.");
        try { path = Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new InvalidOperationException("Toast registration requires a valid executable path.", exception); }

        var name = Path.GetFileNameWithoutExtension(path);
        if (new[] { "dotnet", "dotnet-host", "apphost" }.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Toast registration cannot use a generic .NET host executable.");
        if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) || !_fileExists(path))
            throw new InvalidOperationException("Toast registration requires an existing application executable.");
        return path;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _activator?.Dispose();
            _activator = null;
        }
        // Persistent shortcut and LocalServer32 remain for clicks after exit.
    }
}

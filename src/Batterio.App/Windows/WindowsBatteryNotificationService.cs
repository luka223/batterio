using System.Xml.Linq;
using Batterio.Core.Abstractions;

namespace Batterio.App.Windows;

public sealed class WindowsBatteryNotificationService : IBatteryNotificationService
{
    private readonly WindowsToastRegistrationService _registration;
    private readonly IWindowsToastPlatform _platform;
    private readonly IBatterioLog _log;
    private bool _initialized;

    public WindowsBatteryNotificationService(WindowsToastRegistrationService registration,
        IWindowsToastPlatform platform, IBatterioLog log)
    {
        _registration = registration;
        _platform = platform;
        _log = log;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _registration.RegisterCurrentExecutable();
            _initialized = true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // A developer/debug launch may not have the launcher beside the runtime. Keep
            // the tray monitor usable while persistent activation remains disabled.
            _log.Log(BatterioLogLevel.Warning,
                "Windows notification activation is unavailable until the complete Batterio installation is present.",
                exception);
            _initialized = false;
        }
        return Task.CompletedTask;
    }

    public Task ShowLowBatteryAsync(string deviceId, string deviceName, CancellationToken cancellationToken = default) =>
        ShowAsync(deviceId, deviceName, "low-battery", $"{deviceName} battery level is below 30%", cancellationToken);

    public Task ShowFullyChargedAsync(string deviceId, string deviceName, CancellationToken cancellationToken = default) =>
        ShowAsync(deviceId, deviceName, "fully-charged", $"{deviceName} battery is fully charged", cancellationToken);

    private Task ShowAsync(string deviceId, string deviceName, string notificationKind, string message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(deviceName);
        if (!_initialized) return Task.CompletedTask;

        // Foreground activation targets the registered COM activator, not a URI protocol.
        var xml = new XElement("toast", new XAttribute("activationType", "foreground"),
            new XAttribute("launch", notificationKind),
            new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"),
                new XElement("text", message)))).ToString(SaveOptions.DisableFormatting);
        _platform.Show(WindowsToastRegistrationService.AppUserModelId, xml,
            exception => _log.Log(BatterioLogLevel.Warning,
                $"Windows rejected the {notificationKind} notification for device '{deviceId}'.", exception));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _initialized = false;
        _registration.Dispose();
        return ValueTask.CompletedTask;
    }
}

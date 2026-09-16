namespace Batterio.App.Windows;

public sealed record ToastShortcutRegistration(string ExecutablePath, string AppUserModelId, Guid ActivatorId);

/// <summary>Native operations kept behind a seam for hardware-independent verification.</summary>
public interface IWindowsToastPlatform
{
    void SetProcessAppId(string appId);
    void WriteShortcut(ToastShortcutRegistration registration);
    void WriteComServer(Guid activatorId, string executablePath, string command);
    IDisposable RegisterActivator(Guid activatorId, Action<string, string?> activated);
    void Show(string appId, string xml, Action<Exception> failed);
}

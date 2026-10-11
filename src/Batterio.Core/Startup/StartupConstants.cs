namespace Batterio.Core.Startup;

/// <summary>
/// Names and fixed protocol values shared by the public launcher and the WinUI runtime.
/// </summary>
#if BATTERIO_STARTUP
internal
#else
public
#endif
static class StartupConstants
{
    public const string LauncherExecutableName = "Batterio.App.exe";
    public const string RuntimeExecutableName = "Batterio.Runtime.exe";
    public const string StartupReadyEventEnvironmentVariable = "BATTERIO_STARTUP_READY_EVENT";
    public const string LauncherPathEnvironmentVariable = "BATTERIO_LAUNCHER_PATH";
    public const string SingleInstanceKey = "Batterio.SingleInstance";
    public const string ToastActivationArgument = "-ToastActivated";
    public const string TrayWindowClass = "Shell_TrayWnd";
}

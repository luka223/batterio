using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Xml.Linq;
using Batterio.Core.Abstractions;
using Batterio.Core.Startup;
using Batterio.Core.Updates;

namespace Batterio.App.Windows;

public static class AppUpdateEnvironment
{
    public const string ReleasesPage = "https://github.com/luka223/batterio/releases";

    public static Version CurrentVersion =>
        UpdateVersion.TryParse(typeof(AppUpdateEnvironment).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, out var version)
            ? version
            : new Version(0, 0, 0, 0);

    /// <summary>
    /// The single-file Batterio.exe this runtime was started from, or null for the folder build and
    /// debug runs, which cannot be updated by replacing one file.
    /// </summary>
    public static string? UpdatableExecutablePath
    {
        get
        {
            var path = Environment.GetEnvironmentVariable(StartupConstants.LauncherPathEnvironmentVariable);
            return !string.IsNullOrWhiteSpace(path) &&
                   Path.IsPathFullyQualified(path) &&
                   Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(path)
                ? Path.GetFullPath(path)
                : null;
        }
    }

    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            // Each request applies its own timeout; a 100 MB download must not hit a fixed limit.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Batterio/{CurrentVersion.ToString(3)}");
        return client;
    }
}

/// <summary>Starts the replaced Batterio.exe and exits this copy.</summary>
public sealed class WindowsUpdateRestarter(Func<Task> exit) : IUpdateRestarter
{
    public void Start(string executablePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(StartupConstants.UpdateWaitArgument);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        // The new launcher sets these for its own runtime; it must not inherit this runtime's values.
        startInfo.Environment.Remove(StartupConstants.StartupReadyEventEnvironmentVariable);
        startInfo.Environment.Remove(StartupConstants.LauncherPathEnvironmentVariable);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The updated Batterio.exe could not be started.");
    }

    public Task ExitAsync() => exit();
}

/// <summary>Shows the "update available" notification; clicking it opens Settings.</summary>
public sealed class WindowsUpdateNotifier(
    WindowsToastRegistrationService registration,
    IWindowsToastPlatform platform,
    IBatterioLog log)
{
    public void ShowUpdateAvailable(Version version)
    {
        try
        {
            registration.RegisterCurrentExecutable();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            log.Log(BatterioLogLevel.Warning, "The update notification is unavailable in this installation.", exception);
            return;
        }

        var xml = new XElement("toast", new XAttribute("activationType", "foreground"),
            new XAttribute("launch", WindowsToastRegistrationService.UpdateAvailableLaunchArgument),
            new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"),
                new XElement("text", $"Batterio {version.ToString(3)} is available"),
                new XElement("text", "Open Settings to download and install the update."))))
            .ToString(SaveOptions.DisableFormatting);
        platform.Show(WindowsToastRegistrationService.AppUserModelId, xml,
            exception => log.Log(BatterioLogLevel.Warning, "Windows rejected the update notification.", exception));
    }
}

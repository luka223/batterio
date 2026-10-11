using Batterio.Core.Startup;

namespace Batterio.App.Windows;

public sealed record InstalledExecutablePaths(string LauncherPath, string RuntimePath);

/// <summary>Resolves the two files that must travel together in a Batterio install.</summary>
public static class InstalledExecutableResolver
{
    public static InstalledExecutablePaths Resolve(
        string? baseDirectory = null,
        Func<string, bool>? fileExists = null,
        string? bundledLauncherPath = null)
    {
        fileExists ??= File.Exists;
        var root = baseDirectory ?? AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
        {
            throw new InvalidOperationException("The Batterio installation directory must be fully qualified.");
        }

        try
        {
            root = Path.GetFullPath(root);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException("The Batterio installation directory is invalid.", exception);
        }

        // A single-file Batterio.exe runs the runtime from an unpacked copy and names itself here;
        // startup and toast registration must point at that file, not at the copy.
        bundledLauncherPath ??= Environment.GetEnvironmentVariable(StartupConstants.LauncherPathEnvironmentVariable);
        var launcher = IsUsableBundledLauncher(bundledLauncherPath, fileExists)
            ? Path.GetFullPath(bundledLauncherPath!)
            : Path.GetFullPath(Path.Combine(root, StartupConstants.LauncherExecutableName));
        var runtime = Path.GetFullPath(Path.Combine(root, StartupConstants.RuntimeExecutableName));
        if (string.Equals(launcher, runtime, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The Batterio launcher and runtime paths must be different.");
        }

        if (!fileExists(launcher) || !fileExists(runtime))
        {
            throw new InvalidOperationException(
                $"The Batterio installation requires both '{StartupConstants.LauncherExecutableName}' and '{StartupConstants.RuntimeExecutableName}'.");
        }

        return new InstalledExecutablePaths(launcher, runtime);
    }

    private static bool IsUsableBundledLauncher(string? path, Func<string, bool> fileExists) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathFullyQualified(path) &&
        Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
        fileExists(path);
}

using Batterio.Core.Startup;

namespace Batterio.Startup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var log = new StartupLog();
        try
        {
            var installationDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            var launcherPath = Environment.ProcessPath;
            var payloadDirectory = launcherPath is null
                ? null
                : EmbeddedPayload.TryPrepare(launcherPath, EmbeddedPayload.DefaultCacheRoot, StartupConstants.RuntimeExecutableName);
            if (payloadDirectory is not null)
            {
                installationDirectory = payloadDirectory;
            }

            // A single-file launcher runs the runtime from an unpacked copy; naming this file lets the
            // runtime register it, rather than the copy, for Windows startup and toast activation.
            Environment.SetEnvironmentVariable(
                StartupConstants.LauncherPathEnvironmentVariable,
                payloadDirectory is null ? null : launcherPath);

            var supervisor = new StartupSupervisor(
                installationDirectory,
                Path.Combine(installationDirectory, StartupConstants.RuntimeExecutableName),
                new NativeStartupTaskbar(),
                new NamedStartupReadyEventFactory(),
                new NativeStartupProcessFactory(),
                new StartupDelay(),
                log);

            return supervisor.RunAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            log.UnexpectedFailure(exception.GetType().Name);
            return 1;
        }
    }
}

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

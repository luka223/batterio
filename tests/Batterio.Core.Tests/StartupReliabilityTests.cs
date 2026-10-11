using Batterio.App.Windows;
using Batterio.Core.Startup;

namespace Batterio.Core.Tests;

public sealed class StartupReliabilityTests
{
    [Fact]
    public void TrayRetryUsesTheSelectedBackoffAndCapsAtThirtySeconds()
    {
        var controller = new TrayIconRetryController();

        var delays = Enumerable.Range(0, 7)
            .Select(_ => controller.RecordFailure())
            .ToArray();

        Assert.Equal(
            new[]
            {
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30)
            },
            delays);
        Assert.Equal(7, controller.FailureCount);

        controller.Reset();
        Assert.Equal(TimeSpan.FromSeconds(1), controller.RecordFailure());
    }

    [Fact]
    public void RepeatedTrayRegistrationFailuresFollowBackoffAndRecover()
    {
        var controller = new TrayIconRetryController();
        var outcomes = new Queue<bool>([false, false, true]);
        var attempts = 0;
        var delays = new List<TimeSpan>();

        while (outcomes.Count > 0)
        {
            attempts++;
            if (outcomes.Dequeue())
            {
                controller.Reset();
                break;
            }

            delays.Add(controller.RecordFailure());
        }

        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays);
        Assert.Equal(0, controller.FailureCount);
    }

    [Fact]
    public void ExplorerRestartResetStartsTheNextRetryAtOneSecond()
    {
        var controller = new TrayIconRetryController();
        controller.RecordFailure();
        controller.RecordFailure();

        controller.Reset();
        Assert.Equal(TimeSpan.FromSeconds(1), controller.RecordFailure());
    }

    [Fact]
    public void RetryFailureLoggingIsLimitedToOnePerMinute()
    {
        var controller = new TrayIconRetryController();
        var now = DateTimeOffset.UtcNow;

        Assert.True(controller.ShouldLogFailure(now));
        Assert.False(controller.ShouldLogFailure(now + TimeSpan.FromSeconds(59)));
        Assert.True(controller.ShouldLogFailure(now + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void PendingRetryCanBeCanceledExactlyOnce()
    {
        var controller = new TrayIconRetryController();

        controller.MarkRetryScheduled();

        Assert.True(controller.CancelRetry());
        Assert.False(controller.CancelRetry());
    }

    [Fact]
    public void InstalledResolverRequiresBothExecutablesAndUsesStableNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "Batterio-resolver-" + Guid.NewGuid().ToString("N"));
        var paths = InstalledExecutableResolver.Resolve(root, path =>
            path.EndsWith(StartupConstants.LauncherExecutableName, StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(StartupConstants.RuntimeExecutableName, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(Path.Combine(Path.GetFullPath(root), StartupConstants.LauncherExecutableName), paths.LauncherPath);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), StartupConstants.RuntimeExecutableName), paths.RuntimePath);
    }

    [Fact]
    public void InstalledResolverPrefersTheSingleFileLauncher()
    {
        var root = Path.Combine(Path.GetTempPath(), "Batterio-resolver-" + Guid.NewGuid().ToString("N"));
        var bundled = Path.Combine(Path.GetTempPath(), "Batterio-downloads", "Batterio.exe");
        var paths = InstalledExecutableResolver.Resolve(root, path =>
            path.EndsWith(StartupConstants.RuntimeExecutableName, StringComparison.OrdinalIgnoreCase) ||
            path.Equals(bundled, StringComparison.OrdinalIgnoreCase), bundled);

        Assert.Equal(bundled, paths.LauncherPath);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), StartupConstants.RuntimeExecutableName), paths.RuntimePath);
    }

    [Fact]
    public void InstalledResolverIgnoresAMissingSingleFileLauncher()
    {
        var root = Path.Combine(Path.GetTempPath(), "Batterio-resolver-" + Guid.NewGuid().ToString("N"));
        var bundled = Path.Combine(Path.GetTempPath(), "Batterio-missing", "Batterio.exe");
        var paths = InstalledExecutableResolver.Resolve(root, path =>
            path.EndsWith(StartupConstants.LauncherExecutableName, StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(StartupConstants.RuntimeExecutableName, StringComparison.OrdinalIgnoreCase), bundled);

        Assert.Equal(Path.Combine(Path.GetFullPath(root), StartupConstants.LauncherExecutableName), paths.LauncherPath);
    }

    [Fact]
    public void InstalledResolverRejectsAnIncompleteFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "Batterio-resolver-" + Guid.NewGuid().ToString("N"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            InstalledExecutableResolver.Resolve(root, path =>
                path.EndsWith(StartupConstants.LauncherExecutableName, StringComparison.OrdinalIgnoreCase)));

        Assert.Contains(StartupConstants.RuntimeExecutableName, exception.Message, StringComparison.Ordinal);
    }
}

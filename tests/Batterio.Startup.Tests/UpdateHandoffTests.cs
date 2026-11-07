using System.Diagnostics;
using Batterio.Startup;

namespace Batterio.Startup.Tests;

public sealed class UpdateHandoffTests
{
    [Fact]
    public void WaitArgumentIsRemovedAndItsProcessIdReturned()
    {
        var remaining = UpdateHandoff.TakeWaitProcessId(["-ToastActivated", "--batterio-update-wait", "4242", "x"], out var processId);
        Assert.Equal(4242, processId);
        Assert.Equal(["-ToastActivated", "x"], remaining);
    }

    [Theory]
    [InlineData("--batterio-update-wait")]
    [InlineData("--batterio-update-wait", "abc")]
    [InlineData("--batterio-update-wait", "-5")]
    public void MalformedWaitArgumentIsDroppedWithoutAProcessId(params string[] arguments)
    {
        var remaining = UpdateHandoff.TakeWaitProcessId(arguments, out var processId);
        Assert.Null(processId);
        Assert.DoesNotContain("--batterio-update-wait", remaining);
    }

    [Fact]
    public void ArgumentsWithoutTheWaitArgumentAreUnchanged()
    {
        var remaining = UpdateHandoff.TakeWaitProcessId(["a", "b"], out var processId);
        Assert.Null(processId);
        Assert.Equal(["a", "b"], remaining);
    }

    [Fact]
    public void WaitingForAnExitedProcessReturnsImmediately()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
        process.WaitForExit();
        Assert.True(UpdateHandoff.WaitForExit(process.Id, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void WaitingForARunningProcessTimesOut()
    {
        Assert.False(UpdateHandoff.WaitForExit(Environment.ProcessId, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void PreviousExecutableIsRemoved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "batterio-handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var launcher = Path.Combine(directory, "Batterio.exe");
            File.WriteAllText(launcher, "new");
            File.WriteAllText(launcher + ".old", "old");
            UpdateHandoff.RemovePreviousExecutable(launcher);
            Assert.False(File.Exists(launcher + ".old"));
            Assert.True(File.Exists(launcher));
            UpdateHandoff.RemovePreviousExecutable(launcher);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

using Batterio.Startup;

namespace Batterio.Startup.Tests;

public sealed class StartupLogTests
{
    [Fact]
    public void LogUsesTheInjectedUtcClock()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Batterio-startup-log-clock-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "startup.log");
        var now = new DateTimeOffset(2026, 9, 16, 12, 34, 56, TimeSpan.Zero);
        try
        {
            var log = new StartupLog(path, new FixedTimeProvider(now));
            log.LauncherStarted("1.2.3");

            Assert.StartsWith("2026-09-16T12:34:56.0000000+00:00 version=1.2.3 event=launcher-start", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void LogRetainsBoundedRecordsWithoutArgumentsOrEventNames()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Batterio-startup-log-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "startup.log");
        try
        {
            var log = new StartupLog(path);
            log.LauncherStarted(new string('v', 100_000));
            for (var attempt = 1; attempt <= 900; attempt++)
            {
                log.AttemptCompleted(attempt, 1234, 5, false, "PreReadinessExit");
            }
            log.FinalFailure(5);

            var content = File.ReadAllText(path);
            Assert.True(new FileInfo(path).Length <= 128 * 1024);
            Assert.Contains("event=final-failure attempts=5", content, StringComparison.Ordinal);
            Assert.DoesNotContain("user argument", content, StringComparison.Ordinal);
            Assert.DoesNotContain("Batterio.StartupReady.", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ConcurrentLogInstancesPreserveEveryRecord()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Batterio-startup-log-concurrency-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "startup.log");
        try
        {
            var logs = new[] { new StartupLog(path), new StartupLog(path) };

            Parallel.For(0, 200, index =>
                logs[index % logs.Length].AttemptCompleted(index, 1234, 5, false, $"Failure{index}"));

            var lines = File.ReadAllLines(path);
            Assert.Equal(200, lines.Length);
            for (var index = 0; index < 200; index++)
            {
                Assert.Contains(lines, line => line.Contains($"failure=Failure{index}", StringComparison.Ordinal));
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LogRetriesWhileAnotherWriterOwnsTheFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Batterio-startup-log-lock-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "startup.log");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, "existing record" + Environment.NewLine);
        try
        {
            using var blocker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            var append = Task.Run(() => new StartupLog(path).RuntimeMissing());

            await Task.Delay(100);
            Assert.False(append.IsCompleted);
            blocker.Dispose();
            await append.WaitAsync(TimeSpan.FromSeconds(3));

            var content = File.ReadAllText(path);
            Assert.Contains("existing record", content, StringComparison.Ordinal);
            Assert.Contains("event=runtime-missing", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

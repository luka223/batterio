using Batterio.App.Windows;
using Batterio.Core.Startup;

namespace Batterio.Windows.Tests;

public sealed class StartupReadinessTests
{
    [Fact]
    public void ValidEnvironmentEventIsConsumedAndSignaledIdempotently()
    {
        var name = $"Batterio.StartupReady.{Guid.NewGuid():N}";
        using var eventHandle = new EventWaitHandle(false, EventResetMode.ManualReset, name, out _);
        Environment.SetEnvironmentVariable(StartupConstants.StartupReadyEventEnvironmentVariable, name);

        using var readiness = StartupReadiness.CaptureFromEnvironment();

        Assert.True(readiness.IsSupervised);
        Assert.Null(Environment.GetEnvironmentVariable(StartupConstants.StartupReadyEventEnvironmentVariable));
        Assert.True(readiness.Signal());
        Assert.True(readiness.Signal());
        Assert.True(eventHandle.WaitOne(0));
    }

    [Fact]
    public void MalformedEnvironmentEventCannotClaimReadiness()
    {
        Environment.SetEnvironmentVariable(StartupConstants.StartupReadyEventEnvironmentVariable, "not-a-batterio-event");

        using var readiness = StartupReadiness.CaptureFromEnvironment();

        Assert.True(readiness.IsConfigured);
        Assert.True(readiness.IsSupervised);
        Assert.False(readiness.HasValidEvent);
        Assert.False(readiness.Signal());
    }

    [Fact]
    public void MissingValidNameIsAConfiguredButUnavailableHandshake()
    {
        using var readiness = StartupReadiness.CreateForTesting(
            $"Batterio.StartupReady.{Guid.NewGuid():N}",
            EventWaitHandle.OpenExisting);

        Assert.True(readiness.IsSupervised);
        Assert.False(readiness.HasValidEvent);
        Assert.False(readiness.Signal());
    }

    [Fact]
    public void InaccessibleEventIsDifferentFromMalformedAndValidEvents()
    {
        using var readiness = StartupReadiness.CreateForTesting(
            $"Batterio.StartupReady.{Guid.NewGuid():N}",
            _ => throw new UnauthorizedAccessException());

        Assert.True(readiness.IsConfigured);
        Assert.True(readiness.IsSupervised);
        Assert.False(readiness.HasValidEvent);
        Assert.False(readiness.Signal());
    }

    [Fact]
    public void DisposedAndValidEventsHaveDistinctSignalResults()
    {
        var name = $"Batterio.StartupReady.{Guid.NewGuid():N}";
        using var eventHandle = new EventWaitHandle(false, EventResetMode.ManualReset, name, out _);
        var readiness = StartupReadiness.CreateForTesting(name, EventWaitHandle.OpenExisting);

        Assert.True(readiness.HasValidEvent);
        readiness.Dispose();
        Assert.False(readiness.Signal());
        Assert.False(eventHandle.WaitOne(0));
    }
}

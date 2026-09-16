using Batterio.Core.Startup;

namespace Batterio.Startup.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public async Task PrimaryRegistersActivationAndDoesNotRedirect()
    {
        var host = new FakeHost(isCurrent: true);
        var coordinator = new SingleInstanceCoordinator<string>(host);
        var received = string.Empty;
        var xamlStarted = false;

        var outcome = await coordinator.StartAsync(
            "launch",
            value => received = value,
            startPrimary: () => xamlStarted = true);

        Assert.Equal(SingleInstanceOutcome.Primary, outcome);
        Assert.True(host.Registered);
        Assert.Null(host.RedirectedActivation);
        Assert.True(xamlStarted);
        host.Raise("toast");
        Assert.Equal("toast", received);
    }

    [Fact]
    public async Task SecondaryRedirectsWithoutRegisteringAnActivationHandler()
    {
        var host = new FakeHost(isCurrent: false);
        var coordinator = new SingleInstanceCoordinator<string>(host);
        var xamlStarted = false;

        var outcome = await coordinator.StartAsync(
            "toast",
            _ => { },
            startPrimary: () => xamlStarted = true);

        Assert.Equal(SingleInstanceOutcome.Redirected, outcome);
        Assert.False(host.Registered);
        Assert.False(xamlStarted);
        Assert.Equal("toast", host.RedirectedActivation);
    }

    [Fact]
    public async Task PrimaryStartupLeavesReadinessToThePopupCoordinator()
    {
        var host = new FakeHost(isCurrent: true);
        var policy = new SingleInstanceStartupPolicy<string>(new SingleInstanceCoordinator<string>(host));
        var readinessSignals = 0;
        var xamlStarted = false;

        var ready = await policy.StartAsync(
            "launch",
            _ => { },
            () =>
            {
                readinessSignals++;
                return true;
            },
            startPrimary: () => xamlStarted = true);

        Assert.True(ready);
        Assert.True(xamlStarted);
        Assert.Equal(0, readinessSignals);
    }

    [Fact]
    public async Task SuccessfulSecondaryRedirectSignalsReadinessExactlyOnceWithoutStartingXaml()
    {
        var host = new FakeHost(isCurrent: false);
        var policy = new SingleInstanceStartupPolicy<string>(new SingleInstanceCoordinator<string>(host));
        var readinessSignals = 0;
        var xamlStarted = false;

        var ready = await policy.StartAsync(
            "toast",
            _ => { },
            () =>
            {
                readinessSignals++;
                return true;
            },
            startPrimary: () => xamlStarted = true);

        Assert.True(ready);
        Assert.Equal(1, readinessSignals);
        Assert.False(xamlStarted);
    }

    [Fact]
    public async Task RedirectTimeoutDoesNotSignalReadinessOrStartXaml()
    {
        var host = new FakeHost(isCurrent: false) { NeverCompletesRedirect = true };
        var policy = new SingleInstanceStartupPolicy<string>(
            new SingleInstanceCoordinator<string>(host, TimeSpan.FromMilliseconds(10)));
        var readinessSignals = 0;
        var xamlStarted = false;

        await Assert.ThrowsAsync<TimeoutException>(() => policy.StartAsync(
            "toast",
            _ => { },
            () =>
            {
                readinessSignals++;
                return true;
            },
            startPrimary: () => xamlStarted = true));

        Assert.Equal(0, readinessSignals);
        Assert.False(xamlStarted);
    }

    [Fact]
    public async Task RedirectTimeoutPropagatesAndDoesNotBecomeASecondaryUiStart()
    {
        var host = new FakeHost(isCurrent: false) { NeverCompletesRedirect = true };
        var coordinator = new SingleInstanceCoordinator<string>(host, TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAsync<TimeoutException>(() => coordinator.StartAsync("toast", _ => { }));

        Assert.False(host.Registered);
    }

    [Fact]
    public async Task SynchronousRedirectFailureDoesNotStartPrimaryUi()
    {
        var host = new FakeHost(isCurrent: false)
        {
            RedirectFailure = new InvalidOperationException("redirect failed synchronously")
        };
        var coordinator = new SingleInstanceCoordinator<string>(host);
        var xamlStarted = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync(
            "toast",
            _ => { },
            startPrimary: () => xamlStarted = true));

        Assert.False(host.Registered);
        Assert.False(xamlStarted);
    }

    [Fact]
    public async Task AsynchronousRedirectFailureDoesNotStartPrimaryUi()
    {
        var host = new FakeHost(isCurrent: false)
        {
            RedirectFailure = new IOException("redirect failed asynchronously"),
            RedirectFailureAsynchronously = true
        };
        var coordinator = new SingleInstanceCoordinator<string>(host);
        var xamlStarted = false;

        await Assert.ThrowsAsync<IOException>(() => coordinator.StartAsync(
            "toast",
            _ => { },
            startPrimary: () => xamlStarted = true));

        Assert.False(host.Registered);
        Assert.False(xamlStarted);
    }

    [Fact]
    public async Task RedirectCancellationDoesNotStartPrimaryUi()
    {
        var host = new FakeHost(isCurrent: false) { NeverCompletesRedirect = true };
        var coordinator = new SingleInstanceCoordinator<string>(host);
        var xamlStarted = false;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.StartAsync(
            "toast",
            _ => { },
            startPrimary: () => xamlStarted = true,
            cancellationToken: cancellation.Token));

        Assert.False(host.Registered);
        Assert.False(xamlStarted);
    }

    private sealed class FakeHost(bool isCurrent) : ISingleInstanceHost<string>
    {
        private Action<string>? _activation;
        public bool IsCurrent { get; } = isCurrent;
        public bool Registered { get; private set; }
        public bool NeverCompletesRedirect { get; set; }
        public bool RedirectFailureAsynchronously { get; set; }
        public Exception? RedirectFailure { get; set; }
        public string? RedirectedActivation { get; private set; }

        public void RegisterActivation(Action<string> activated)
        {
            Registered = true;
            _activation = activated;
        }

        public Task RedirectActivationAsync(string activation, CancellationToken cancellationToken = default)
        {
            RedirectedActivation = activation;
            if (RedirectFailure is { } failure)
            {
                return RedirectFailureAsynchronously
                    ? Task.FromException(failure)
                    : throw failure;
            }

            return NeverCompletesRedirect
                ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                : Task.CompletedTask;
        }

        public void Raise(string activation) => _activation?.Invoke(activation);
    }
}

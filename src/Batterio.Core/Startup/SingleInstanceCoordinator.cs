namespace Batterio.Core.Startup;

/// <summary>Small adapter seam around the Windows App SDK instance APIs.</summary>
public interface ISingleInstanceHost<TActivation>
{
    bool IsCurrent { get; }

    void RegisterActivation(Action<TActivation> activated);

    Task RedirectActivationAsync(TActivation activation, CancellationToken cancellationToken = default);
}

public enum SingleInstanceOutcome
{
    Primary,
    Redirected
}

/// <summary>
/// Performs instance arbitration before any UI framework initialization.
/// </summary>
public sealed class SingleInstanceCoordinator<TActivation>
{
    private readonly ISingleInstanceHost<TActivation> _host;
    private readonly TimeSpan _redirectTimeout;

    public SingleInstanceCoordinator(ISingleInstanceHost<TActivation> host,
        TimeSpan? redirectTimeout = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _redirectTimeout = redirectTimeout ?? TimeSpan.FromSeconds(10);
        if (_redirectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(redirectTimeout));
        }
    }

    public async Task<SingleInstanceOutcome> StartAsync(
        TActivation activation,
        Action<TActivation> activated,
        Action? startPrimary = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activated);
        if (!_host.IsCurrent)
        {
            var redirect = _host.RedirectActivationAsync(activation, cancellationToken);
            await redirect.WaitAsync(_redirectTimeout, cancellationToken).ConfigureAwait(false);
            return SingleInstanceOutcome.Redirected;
        }

        _host.RegisterActivation(activated);
        startPrimary?.Invoke();
        return SingleInstanceOutcome.Primary;
    }
}

/// <summary>
/// Signals the launcher's readiness only for a successfully redirected secondary process.
/// The primary runtime signals from its UI startup coordinator after the popup is ready.
/// </summary>
public sealed class SingleInstanceStartupPolicy<TActivation>
{
    private readonly SingleInstanceCoordinator<TActivation> _coordinator;

    public SingleInstanceStartupPolicy(SingleInstanceCoordinator<TActivation> coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    public async Task<bool> StartAsync(
        TActivation activation,
        Action<TActivation> activated,
        Func<bool> signalRedirectedReadiness,
        Action? startPrimary = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activated);
        ArgumentNullException.ThrowIfNull(signalRedirectedReadiness);

        var outcome = await _coordinator.StartAsync(
            activation,
            activated,
            startPrimary,
            cancellationToken).ConfigureAwait(false);
        return outcome == SingleInstanceOutcome.Primary || signalRedirectedReadiness();
    }
}

namespace Batterio.Core.Startup;

/// <summary>Roots a primary application instance and its exact activation subscription.</summary>
public sealed class PrimaryAppInstanceRegistration<TInstance> : IDisposable
    where TInstance : class
{
    private readonly TInstance _instance;
    private readonly Action _subscribe;
    private Action? _unsubscribe;
    private int _subscribed;

    public PrimaryAppInstanceRegistration(
        TInstance instance,
        Action subscribe,
        Action unsubscribe)
    {
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
        _subscribe = subscribe ?? throw new ArgumentNullException(nameof(subscribe));
        _unsubscribe = unsubscribe ?? throw new ArgumentNullException(nameof(unsubscribe));
    }

    public void Subscribe()
    {
        if (Interlocked.Exchange(ref _subscribed, 1) != 0)
        {
            throw new InvalidOperationException("The primary activation handler is already registered.");
        }

        try
        {
            _subscribe();
        }
        catch
        {
            Interlocked.Exchange(ref _subscribed, 0);
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _subscribed, 0) != 0)
        {
            Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
        }

        GC.KeepAlive(_instance);
    }
}

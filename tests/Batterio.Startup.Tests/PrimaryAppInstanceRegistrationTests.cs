using Batterio.Core.Startup;

namespace Batterio.Startup.Tests;

public sealed class PrimaryAppInstanceRegistrationTests
{
    [Fact]
    public void RegistrationRootsTheInstanceAndUnsubscribesTheExactHandlerOnDispose()
    {
        var instance = new FakeInstance();
        var weakInstance = new WeakReference(instance);
        var activations = new List<string>();
        EventHandler<string> handler = (_, activation) => activations.Add(activation);
        var registration = new PrimaryAppInstanceRegistration<FakeInstance>(
            instance,
            () => instance.Activated += handler,
            () => instance.Activated -= handler);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.True(weakInstance.IsAlive);
        registration.Subscribe();
        instance.Raise("before-close");
        registration.Dispose();
        instance.Raise("after-close");

        Assert.Equal(["before-close"], activations);
    }

    private sealed class FakeInstance
    {
        public event EventHandler<string>? Activated;

        public void Raise(string activation) => Activated?.Invoke(this, activation);
    }
}

using Batterio.App.Windows;
using Batterio.Core.Startup;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinRT;

namespace Batterio.App.WinUI;

internal static class Program
{
    private static PrimaryAppInstanceRegistration<AppInstance>? s_primaryRegistration;

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();
        using var readiness = StartupReadiness.CaptureFromEnvironment();

        try
        {
            var current = AppInstance.GetCurrent();
            var activation = current.GetActivatedEventArgs();
            var instance = AppInstance.FindOrRegisterForKey(StartupConstants.SingleInstanceKey);
            var coordinator = new SingleInstanceCoordinator<AppActivationArguments>(
                new WindowsAppInstanceHost(instance));
            var startupPolicy = new SingleInstanceStartupPolicy<AppActivationArguments>(coordinator);
            var readinessSignaled = await startupPolicy.StartAsync(
                activation,
                _ => WindowsToastActivation.Current.RequestPopup(),
                readiness.Signal,
                startPrimary: () =>
                {
                    if (args.Any(argument => string.Equals(
                            argument,
                            StartupConstants.ToastActivationArgument,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        WindowsToastActivation.Current.RequestPopup();
                    }

                    var registration = Volatile.Read(ref s_primaryRegistration)
                        ?? throw new InvalidOperationException("The primary AppInstance registration was not retained.");
                    Microsoft.UI.Xaml.Application.Start(_ =>
                    {
                        var context = new DispatcherQueueSynchronizationContext(
                            DispatcherQueue.GetForCurrentThread());
                        SynchronizationContext.SetSynchronizationContext(context);
                        new App();
                    });
                    GC.KeepAlive(registration);
                }).ConfigureAwait(false);

            return readinessSignaled ? 0 : 1;
        }
        catch
        {
            return 1;
        }
        finally
        {
            var registration = Interlocked.Exchange(ref s_primaryRegistration, null);
            try
            {
                registration?.Dispose();
            }
            finally
            {
                GC.KeepAlive(registration);
            }
        }
    }

    private sealed class WindowsAppInstanceHost(AppInstance instance)
        : ISingleInstanceHost<AppActivationArguments>
    {
        public bool IsCurrent => instance.IsCurrent;

        public void RegisterActivation(Action<AppActivationArguments> activated)
        {
            EventHandler<AppActivationArguments> handler = (_, arguments) => activated(arguments);
            var registration = new PrimaryAppInstanceRegistration<AppInstance>(
                instance,
                () => instance.Activated += handler,
                () => instance.Activated -= handler);

            if (Interlocked.CompareExchange(ref s_primaryRegistration, registration, null) is not null)
            {
                throw new InvalidOperationException("The primary AppInstance registration is already installed.");
            }

            registration.Subscribe();
        }

        public async Task RedirectActivationAsync(
            AppActivationArguments activation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await instance.RedirectActivationToAsync(activation);
        }
    }
}

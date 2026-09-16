using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace Batterio.App.WinUI;

public partial class App : MauiWinUIApplication
{
    private readonly AppInstance _instance;

    public App()
    {
        InitializeComponent();
        _instance = AppInstance.FindOrRegisterForKey("Batterio.SingleInstance");
        if (!_instance.IsCurrent)
        {
            _ = RedirectToExistingInstanceAsync();
            return;
        }

        AppInstance.GetCurrent().Activated += OnActivated;
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Redirecting instances must never create a second tray or start polling.
        if (_instance.IsCurrent) base.OnLaunched(args);
    }

    private async Task RedirectToExistingInstanceAsync()
    {
        try
        {
            await _instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    private void OnActivated(object? sender, AppActivationArguments args)
    {
        Batterio.App.Windows.WindowsToastActivation.Current.RequestPopup();
    }
}


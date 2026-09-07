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
        Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
        {
            if (Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services.GetService(typeof(Batterio.App.Windows.WindowCoordinator)) is Batterio.App.Windows.WindowCoordinator windows)
            {
                windows.ShowPopup();
            }
        });
    }
}


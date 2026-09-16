using Batterio.App.Windows;

namespace Batterio.App;

public partial class App : Application
{
    private readonly BatteryApplication _application;
    private readonly WindowCoordinator _windows;
    private readonly RuntimeStartupCompletionCoordinator _startupCompletion;

    public App(BatteryApplication application, WindowCoordinator windows, StartupReadiness readiness)
    {
        InitializeComponent();
        _application = application;
        _windows = windows;
        _startupCompletion = new RuntimeStartupCompletionCoordinator(
            _application.StartAsync,
            SetActivationReady,
            readiness.Signal,
            readiness.IsSupervised,
            exception =>
            {
                _windows.ReportStartupError(exception.Message);
                _windows.ShowPopup();
            },
            _application.ExitAsync);
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var popup = new Window(new PopupPage(_application.ViewModel)) { Title = "Batterio" };
        _windows.RegisterPopupWindow(popup);
        popup.Created += OnPopupCreated;
        return popup;
    }

    private async void OnPopupCreated(object? sender, EventArgs e)
    {
        if (sender is Window popup)
        {
            popup.Created -= OnPopupCreated;
        }

        await _startupCompletion.CompleteAsync();
    }

    private void SetActivationReady()
    {
        WindowsToastActivation.Current.SetReady(() => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_application.IsExiting) _windows.ShowPopup();
        }));
    }
}

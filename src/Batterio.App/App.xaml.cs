using Batterio.App.Windows;

namespace Batterio.App;

public partial class App : Application
{
    private readonly BatteryApplication _application;
    private readonly WindowCoordinator _windows;

    public App(BatteryApplication application, WindowCoordinator windows)
    {
        InitializeComponent();
        _application = application;
        _windows = windows;
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

        await _application.StartAsync();
    }
}

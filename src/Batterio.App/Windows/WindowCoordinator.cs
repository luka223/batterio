using Batterio.Core;
using Batterio.Core.Abstractions;
using Microsoft.UI.Windowing;
using WinRT.Interop;

namespace Batterio.App.Windows;

public enum TrayMenuAction
{
    ShowPopup,
    ShowSettings,
    RefreshAll,
    Exit
}

public sealed class WindowCoordinator : IWindowCoordinator
{
    private const nuint SettingsMenuCommandId = 1;
    private const nuint CloseMenuCommandId = 2;

    private readonly ITrayService _tray;
    private readonly DeviceSelectionCoordinator? _selection;
    private readonly IServiceProvider? _services;
    private BatteryViewModel? _viewModel;

    private Window? _popupWindow;
    private Window? _settingsWindow;
    private bool _showRequested;
    private bool _allowClose;

    public WindowCoordinator(
        ITrayService tray,
        DeviceSelectionCoordinator? selection = null,
        IServiceProvider? services = null)
    {
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _selection = selection;
        _services = services;
    }

    public event EventHandler<TrayMenuAction>? MenuActionRequested;

    public void SetViewModel(BatteryViewModel viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.DevicesChanged -= OnDevicesChanged;
        }

        _viewModel = viewModel;
        _viewModel.DevicesChanged += OnDevicesChanged;
    }

    public void RegisterPopupWindow(Window window)
    {
        _popupWindow = window;
        window.Created += (_, _) =>
        {
            ConfigurePopupWindow(window);
            if (GetAppWindow(window) is { } appWindow)
            {
                appWindow.Closing += OnPopupClosing;
            }
            HidePopup();
        };
        // MAUI activates the initial window after creation. Keep startup tray-only.
        window.Activated += OnInitialActivation;
        // A tray popup is dismissed as soon as another window is activated, including
        // when the user clicks anywhere outside the popup.
        window.Deactivated += OnPopupDeactivated;
    }

    private void OnInitialActivation(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.Activated -= OnInitialActivation;
            if (!_showRequested)
            {
                HidePopup();
            }
        }
    }

    private void OnPopupDeactivated(object? sender, EventArgs e) => HidePopup();

    public void TogglePopup()
    {
        if (_popupWindow is null || GetAppWindow(_popupWindow) is null)
        {
            ShowPopup();
            return;
        }

        if (GetAppWindow(_popupWindow)!.IsVisible)
        {
            HidePopup();
        }
        else
        {
            ShowPopup();
        }
    }

    public void ShowPopup()
    {
        if (_popupWindow is null)
        {
            return;
        }

        _showRequested = true;
        _ = RequestDiscoveryRefreshAsync();
        PositionPopup(_popupWindow);
        GetAppWindow(_popupWindow)?.Show();
        Activate(_popupWindow);
    }

    public async Task ShowSettingsAsync()
    {
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (_settingsWindow is { } existing)
            {
                GetAppWindow(existing)?.Show();
                Activate(existing);
                if (existing.Page?.BindingContext is SettingsViewModel existingViewModel)
                {
                    _ = existingViewModel.RefreshOnActivationAsync();
                }

                return;
            }

            var services = _services ?? Application.Current?.Handler?.MauiContext?.Services;
            var viewModel = services?.GetService(typeof(SettingsViewModel)) as SettingsViewModel;
            if (viewModel is null)
            {
                return;
            }

            var settings = new Window(new SettingsPage(viewModel)) { Title = "Batterio Settings" };
            _settingsWindow = settings;
            settings.Created += OnSettingsCreated;
            settings.Activated += OnSettingsActivated;
            settings.Destroying += OnSettingsDestroying;
            Application.Current?.OpenWindow(settings);
            _ = viewModel.InitializeAsync();
        });
    }

    public void HidePopup()
    {
        if (_popupWindow is not null)
        {
            GetAppWindow(_popupWindow)?.Hide();
        }
    }

    public void ShowTrayMenu()
    {
        HidePopup();
        if (_popupWindow?.Handler?.PlatformView is not { } owner)
        {
            return;
        }

        var handle = WindowNative.GetWindowHandle(owner);
        if (!Win32Native.GetCursorPos(out var point))
        {
            return;
        }

        var menu = Win32Native.CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        uint selected = 0;
        try
        {
            if (!Win32Native.AppendMenu(menu, Win32Native.MF_STRING, SettingsMenuCommandId, "Settings") ||
                !Win32Native.AppendMenu(menu, Win32Native.MF_STRING, CloseMenuCommandId, "Close"))
            {
                return;
            }

            // Native tray menus need a foreground owner to dismiss on outside clicks.
            Win32Native.SetForegroundWindow(handle);
            selected = Win32Native.TrackPopupMenu(
                menu,
                Win32Native.TPM_RIGHTBUTTON | Win32Native.TPM_RETURNCMD | Win32Native.TPM_NONOTIFY,
                point.X, point.Y, 0, handle, 0);
        }
        finally
        {
            Win32Native.DestroyMenu(menu);
            Win32Native.PostMessage(handle, Win32Native.WM_NULL, 0, 0);
        }

        if (selected == SettingsMenuCommandId)
        {
            MenuActionRequested?.Invoke(this, TrayMenuAction.ShowSettings);
        }
        else if (selected == CloseMenuCommandId)
        {
            MenuActionRequested?.Invoke(this, TrayMenuAction.Exit);
        }
    }

    public void ReportStartupError(string message)
    {
        _viewModel?.SetStartupError(message);
    }

    public async Task CloseAsync()
    {
        _allowClose = true;
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (_settingsWindow is not null)
            {
                Application.Current?.CloseWindow(_settingsWindow);
            }

            if (_popupWindow is not null)
            {
                Application.Current?.CloseWindow(_popupWindow);
                _popupWindow = null;
            }
        });
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        if (_popupWindow is { } popup && GetAppWindow(popup) is { IsVisible: true })
        {
            PositionPopup(popup);
        }
    }

    private async Task RequestDiscoveryRefreshAsync()
    {
        if (_selection is null)
        {
            return;
        }

        try
        {
            await _selection.RefreshDiscoveryAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Discovery errors are represented on the settings rows and do not prevent
            // the popup from opening.
        }
    }

    private void OnSettingsCreated(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            ConfigureSettingsWindow(window);
        }
    }

    private void OnSettingsActivated(object? sender, EventArgs e)
    {
        if (sender is Window { Page.BindingContext: SettingsViewModel viewModel })
        {
            _ = viewModel.RefreshOnActivationAsync();
        }
    }

    private void OnSettingsDestroying(object? sender, EventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        window.Created -= OnSettingsCreated;
        window.Activated -= OnSettingsActivated;
        window.Destroying -= OnSettingsDestroying;
        if (window.Page?.BindingContext is SettingsViewModel viewModel)
        {
            viewModel.Dispose();
        }

        if (ReferenceEquals(_settingsWindow, window))
        {
            _settingsWindow = null;
        }
    }

    private void ConfigureSettingsWindow(Window window)
    {
        if (GetAppWindow(window) is not { } appWindow)
        {
            return;
        }

        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "batterio.ico"));
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = true;
        appWindow.SetPresenter(presenter);
        appWindow.IsShownInSwitchers = true;
        var scale = GetDpiScale(window);
        appWindow.Resize(new global::Windows.Graphics.SizeInt32(
            (int)Math.Round(720 * scale),
            (int)Math.Round(600 * scale)));
    }

    private void OnPopupClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_allowClose)
        {
            args.Cancel = true;
            sender.Hide();
        }
    }

    private double PopupHeight => Math.Clamp(86 + (_viewModel?.Devices.Count ?? 1) * 72, 180, 430);

    private double PopupWidth
    {
        get
        {
            var devices = _viewModel?.Devices ?? [];
            var contentWidth = devices.Count == 0
                ? 180
                : devices.Max(device => 56 + 10 + (device.Name.Length * 7.5) + 10 + 42 + 10 + (device.PercentageText.Length * 7.2));

            // Include the page padding and keep the header from becoming narrower than its
            // own content when there are no device rows yet.
            return Math.Max(180, Math.Ceiling(contentWidth + 28));
        }
    }

    private void ConfigurePopupWindow(Window window)
    {
        var appWindow = GetAppWindow(window);
        if (appWindow is null)
        {
            return;
        }

        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "batterio.ico"));
        appWindow.SetPresenter(OverlappedPresenter.CreateForContextMenu());
        appWindow.IsShownInSwitchers = false;
        var scale = GetDpiScale(window);
        appWindow.Resize(new global::Windows.Graphics.SizeInt32(
            (int)Math.Round(PopupWidth * scale),
            (int)Math.Round(PopupHeight * scale)));
    }

    private void PositionPopup(Window window)
    {
        var appWindow = GetAppWindow(window);
        if (appWindow is null)
        {
            return;
        }

        var placement = _tray.GetIconPlacement();
        var scale = GetDpiScale(window);
        var width = (int)Math.Round(PopupWidth * scale);
        var height = (int)Math.Round(PopupHeight * scale);
        appWindow.Resize(new global::Windows.Graphics.SizeInt32(width, height));
        var x = placement?.IconRight - width ?? placement?.WorkAreaRight - width ?? 0;
        var y = placement?.IconTop - height - 8 ?? placement?.WorkAreaBottom - height - 8 ?? 0;
        if (placement is { } bounds)
        {
            x = Math.Clamp(x, bounds.WorkAreaLeft, Math.Max(bounds.WorkAreaLeft, bounds.WorkAreaRight - width));
            y = Math.Clamp(y, bounds.WorkAreaTop, Math.Max(bounds.WorkAreaTop, bounds.WorkAreaBottom - height));
        }

        appWindow.Move(new global::Windows.Graphics.PointInt32(x, y));
    }

    private static AppWindow? GetAppWindow(Window window)
    {
        if (window.Handler?.PlatformView is null)
        {
            return null;
        }

        var handle = WindowNative.GetWindowHandle(window.Handler.PlatformView);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
        return AppWindow.GetFromWindowId(windowId);
    }

    private static void Activate(Window window)
    {
        if (window.Handler?.PlatformView is not null)
        {
            var handle = WindowNative.GetWindowHandle(window.Handler.PlatformView);
            Win32Native.SetForegroundWindow(handle);
        }
    }

    private static double GetDpiScale(Window window)
    {
        if (window.Handler?.PlatformView is null)
        {
            return 1;
        }

        var handle = WindowNative.GetWindowHandle(window.Handler.PlatformView);
        var dpi = Win32Native.GetDpiForWindow(handle);
        return dpi == 0 ? 1 : dpi / 96d;
    }

}




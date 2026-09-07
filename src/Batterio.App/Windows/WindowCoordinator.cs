using Batterio.Core;
using Batterio.Core.Abstractions;
using Microsoft.UI.Windowing;
using WinRT.Interop;

namespace Batterio.App.Windows;

public enum TrayMenuAction
{
    ShowPopup,
    RefreshAll,
    Exit
}

public sealed class WindowCoordinator : IWindowCoordinator
{
    private readonly ITrayService _tray;
    private BatteryViewModel? _viewModel;

    private Window? _popupWindow;
    private bool _showRequested;
    private bool _allowClose;

    public WindowCoordinator(ITrayService tray)
    {
        _tray = tray;
    }

    public event EventHandler<TrayMenuAction>? MenuActionRequested;

    public void SetViewModel(BatteryViewModel viewModel) => _viewModel = viewModel;

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
        PositionPopup(_popupWindow);
        GetAppWindow(_popupWindow)?.Show();
        Activate(_popupWindow);
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
            if (!Win32Native.AppendMenu(menu, Win32Native.MF_STRING, 1, "Close"))
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

        if (selected == 1)
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
            if (_popupWindow is not null)
            {
                Application.Current?.CloseWindow(_popupWindow);
                _popupWindow = null;
            }
        });
    }

    private void OnPopupClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_allowClose)
        {
            args.Cancel = true;
            sender.Hide();
        }
    }

    private double PopupHeight => Math.Clamp(114 + (_viewModel?.Devices.Count ?? 1) * 72, 210, 430);

    private double PopupWidth
    {
        get
        {
            var devices = _viewModel?.Devices ?? [];
            var contentWidth = devices.Count == 0
                ? 180
                : devices.Max(device => 56 + 10 + (device.Name.Length * 7.5) + 10 + 42 + 10 + (device.StatusText.Length * 7.2));

            // Include the page padding and keep the header and footer from becoming narrower
            // than their own content when there are no device rows yet.
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




using Batterio.Core;
using Batterio.Core.Abstractions;
using Batterio.Core.Notifications;
using Batterio.App.Windows;
using Microsoft.Extensions.Logging;

namespace Batterio.App;

public sealed class BatteryApplication : IAsyncDisposable
{
    private readonly PollingCoordinator _polling;
    private readonly ITrayService _tray;
    private readonly WindowCoordinator _windows;
    private readonly DeviceSelectionCoordinator _selection;
    private readonly LowBatteryNotificationCoordinator _lowBatteryNotifications;
    private readonly IBatterioLog _log;
    private readonly ILogger<BatteryApplication> _logger;
    private bool _started;
    private bool _isExiting;

    public BatteryApplication(
        PollingCoordinator polling,
        DeviceSelectionCoordinator selection,
        LowBatteryNotificationCoordinator lowBatteryNotifications,
        ITrayService tray,
        WindowCoordinator windows,
        IBatterioLog log,
        ILogger<BatteryApplication> logger)
    {
        _polling = polling;
        _selection = selection;
        _lowBatteryNotifications = lowBatteryNotifications;
        _tray = tray;
        _windows = windows;
        _log = log;
        _logger = logger;
        ViewModel = new BatteryViewModel(_polling);
        _windows.SetViewModel(ViewModel);
        _tray.ActionRaised += OnTrayAction;
        _tray.SystemResumed += OnSystemResumed;
        _windows.MenuActionRequested += OnMenuAction;
    }

    public BatteryViewModel ViewModel { get; }

    public bool IsExiting => _isExiting;

    public async Task StartAsync()
    {
        if (_started)
        {
            _windows.HidePopup();
            return;
        }

        _started = true;
        _windows.HidePopup();
        try
        {
            await _tray.StartAsync().ConfigureAwait(false);
            await _lowBatteryNotifications.StartAsync().ConfigureAwait(false);
            await _selection.StartAsync().ConfigureAwait(false);
            _logger.LogInformation("Batterio started with receiver discovery enabled.");
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Critical, "Batterio failed to start.", exception);
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                _windows.ReportStartupError(exception.Message);
                _windows.ShowPopup();
            });
        }
    }

    public async Task ExitAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        WindowsToastActivation.Current.Stop();
        _log.Log(BatterioLogLevel.Information, "Exit requested.");
        try
        {
            await _selection.StopAsync().ConfigureAwait(false);
            await _lowBatteryNotifications.StopAsync().ConfigureAwait(false);
            await _polling.StopAsync(TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);
            await _tray.StopAsync().ConfigureAwait(false);
            await _windows.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Error, "Shutdown cleanup encountered an error.", exception);
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        WindowsToastActivation.Current.Stop();
        _tray.ActionRaised -= OnTrayAction;
        _tray.SystemResumed -= OnSystemResumed;
        _windows.MenuActionRequested -= OnMenuAction;
        await _lowBatteryNotifications.DisposeAsync().ConfigureAwait(false);
        await _selection.DisposeAsync().ConfigureAwait(false);
        await _polling.DisposeAsync().ConfigureAwait(false);
        await _tray.DisposeAsync().ConfigureAwait(false);
    }

    private void OnTrayAction(object? sender, TrayAction action)
    {
        _ = MainThread.InvokeOnMainThreadAsync(async () =>
        {
            switch (action)
            {
                case TrayAction.TogglePopup:
                    _windows.TogglePopup();
                    break;
                case TrayAction.ShowPopup:
                    _windows.ShowPopup();
                    break;
                case TrayAction.ShowMenu:
                    _windows.ShowTrayMenu();
                    break;
            }

            await Task.CompletedTask;
        });
    }

    private void OnMenuAction(object? sender, TrayMenuAction action)
    {
        _ = action switch
        {
            TrayMenuAction.ShowPopup => MainThread.InvokeOnMainThreadAsync(() => _windows.ShowPopup()),
            TrayMenuAction.ShowSettings => _windows.ShowSettingsAsync(),
            TrayMenuAction.RefreshAll => _polling.RefreshAllAsync(),
            TrayMenuAction.Exit => ExitAsync(),
            _ => Task.CompletedTask
        };
    }

    private void OnSystemResumed(object? sender, EventArgs e)
    {
        _ = RefreshAfterResumeAsync();
    }

    private async Task RefreshAfterResumeAsync()
    {
        try
        {
            await _selection.RefreshDiscoveryAsync().ConfigureAwait(false);
            await _polling.RefreshAllAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Log(BatterioLogLevel.Warning, "Resume refresh failed.", exception);
        }
    }
}




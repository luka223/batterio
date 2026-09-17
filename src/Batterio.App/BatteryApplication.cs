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
    private readonly BatteryNotificationCoordinator _batteryNotifications;
    private readonly IBatterioLog _log;
    private readonly ILogger<BatteryApplication> _logger;
    private readonly RuntimeStartupSequence _startup;
    private IProcessTerminator _processTerminator = new EnvironmentProcessTerminator();
    private BoundedCleanupProcessExit _boundedExit;
    private bool _isExiting;

    public const int StartupFailureExitCode = 1;
    private static readonly TimeSpan StartupFailureCleanupTimeout = TimeSpan.FromSeconds(3);

    public BatteryApplication(
        PollingCoordinator polling,
        DeviceSelectionCoordinator selection,
        BatteryNotificationCoordinator batteryNotifications,
        ITrayService tray,
        WindowCoordinator windows,
        IBatterioLog log,
        ILogger<BatteryApplication> logger)
    {
        _polling = polling;
        _selection = selection;
        _batteryNotifications = batteryNotifications;
        _tray = tray;
        _windows = windows;
        _log = log;
        _logger = logger;
        _boundedExit = new BoundedCleanupProcessExit(_processTerminator);
        ViewModel = new BatteryViewModel(_polling);
        _windows.SetViewModel(ViewModel);
        _tray.ActionRaised += OnTrayAction;
        _tray.SystemResumed += OnSystemResumed;
        _windows.MenuActionRequested += OnMenuAction;
        _startup = new RuntimeStartupSequence(
            _windows.HidePopup,
            () => _tray.StartAsync(),
            () => _batteryNotifications.StartAsync(),
            StartSelectionAndPollingAsync,
            exception => _log.Log(BatterioLogLevel.Critical, "Batterio failed to start.", exception));
    }

    public BatteryViewModel ViewModel { get; }

    public bool IsExiting => _isExiting;

    public Task StartAsync() => _startup.StartAsync();

    internal void ConfigureProcessTerminator(IProcessTerminator processTerminator)
    {
        _processTerminator = processTerminator ?? throw new ArgumentNullException(nameof(processTerminator));
        _boundedExit = new BoundedCleanupProcessExit(_processTerminator);
    }

    private async Task StartSelectionAndPollingAsync()
    {
        await _selection.StartAsync().ConfigureAwait(false);
        _logger.LogInformation("Batterio started with receiver discovery enabled.");
    }

    public async Task ExitAsync(int exitCode = 0)
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        WindowsToastActivation.Current.Stop();
        _log.Log(BatterioLogLevel.Information, "Exit requested.");
        await _boundedExit.RunAsync(
            CleanupAsync,
            exitCode == StartupFailureExitCode ? StartupFailureCleanupTimeout : TimeSpan.FromSeconds(5),
            exitCode,
            exception => _log.Log(BatterioLogLevel.Warning,
                "Application cleanup exceeded its bounded shutdown time.", exception),
            exception => _log.Log(BatterioLogLevel.Error,
                "Shutdown cleanup encountered an error.", exception)).ConfigureAwait(false);
    }

    private async Task CleanupAsync()
    {
        await _selection.StopAsync().ConfigureAwait(false);
        await _batteryNotifications.StopAsync().ConfigureAwait(false);
        await _polling.StopAsync(TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);
        await _tray.StopAsync().ConfigureAwait(false);
        await _windows.CloseAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        WindowsToastActivation.Current.Stop();
        _tray.ActionRaised -= OnTrayAction;
        _tray.SystemResumed -= OnSystemResumed;
        _windows.MenuActionRequested -= OnMenuAction;
        await _batteryNotifications.DisposeAsync().ConfigureAwait(false);
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




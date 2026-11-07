using System.Reflection;

namespace Batterio.App;

public partial class SettingsPage : ContentPage
{
    private SettingsViewModel? ViewModel => BindingContext as SettingsViewModel;

    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
        VersionLabel.Text = $"Version {GetDisplayVersion()}";
    }

    private static string GetDisplayVersion()
    {
        var version = typeof(SettingsPage).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var metadataStart = version.IndexOf('+');
        return metadataStart >= 0 ? version[..metadataStart] : version;
    }

    private void OnCheckForUpdatesClicked(object? sender, EventArgs e)
    {
        if (ViewModel is { CanCheckForUpdates: true } viewModel)
        {
            _ = viewModel.CheckForUpdatesAsync();
        }
    }

    private void OnDownloadUpdateClicked(object? sender, EventArgs e)
    {
        if (ViewModel is { CanDownloadUpdate: true } viewModel)
        {
            _ = viewModel.DownloadUpdateAsync();
        }
    }

    private void OnInstallUpdateClicked(object? sender, EventArgs e)
    {
        if (ViewModel is { CanInstallUpdate: true } viewModel)
        {
            _ = viewModel.InstallUpdateAsync();
        }
    }

    private void OnOpenReleasesPageClicked(object? sender, EventArgs e)
    {
        _ = SettingsViewModel.OpenReleasesPageAsync();
    }

    private void OnStartupCheckedChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (ViewModel is not null && !ViewModel.IsStartupPending && !ViewModel.IsApplyingStartup)
        {
            _ = ViewModel.SetStartupRegisteredAsync(e.Value);
        }
    }

    private void OnDeviceCheckedChanged(object? sender, CheckedChangedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel?.IsApplyingSnapshot == true ||
            sender is not CheckBox { BindingContext: DeviceSelectionItemViewModel item })
        {
            return;
        }

        if (viewModel is not null)
        {
            _ = viewModel.SetDeviceSelectedAsync(item, e.Value);
        }
    }

    private void OnTaskbarCheckedChanged(object? sender, CheckedChangedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel?.IsApplyingSnapshot == true ||
            sender is not CheckBox { BindingContext: DeviceSelectionItemViewModel item })
        {
            return;
        }

        if (viewModel is not null)
        {
            _ = viewModel.SetDeviceOnTaskbarAsync(item, e.Value);
        }
    }
}

namespace Batterio.App;

public partial class SettingsPage : ContentPage
{
    private SettingsViewModel? ViewModel => BindingContext as SettingsViewModel;

    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
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
}

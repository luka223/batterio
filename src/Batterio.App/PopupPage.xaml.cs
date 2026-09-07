namespace Batterio.App;

public partial class PopupPage : ContentPage
{
    private CancellationTokenSource? _refreshAnimationCancellation;
    private BatteryViewModel? _viewModel;

    public PopupPage(BatteryViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        BindingContext = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_viewModel?.IsRefreshing == true)
        {
            StartRefreshAnimation();
        }
    }

    protected override void OnDisappearing()
    {
        StopRefreshAnimation();
        base.OnDisappearing();
    }

    protected override void OnHandlerChanged()
    {
        if (Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement previous)
        {
            previous.KeyDown -= OnNativeKeyDown;
        }

        base.OnHandlerChanged();

        if (Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement current)
        {
            current.KeyDown += OnNativeKeyDown;
        }
    }

    private void OnNativeKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (args.Key == global::Windows.System.VirtualKey.Escape &&
            Application.Current?.Handler?.MauiContext?.Services.GetService(typeof(Batterio.App.Windows.WindowCoordinator)) is Batterio.App.Windows.WindowCoordinator windows)
        {
            windows.HidePopup();
            args.Handled = true;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(BatteryViewModel.IsRefreshing))
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_viewModel?.IsRefreshing == true)
            {
                StartRefreshAnimation();
            }
            else
            {
                StopRefreshAnimation();
            }
        });
    }

    private void StartRefreshAnimation()
    {
        if (_refreshAnimationCancellation is not null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _refreshAnimationCancellation = cancellation;
        _ = AnimateRefreshIconAsync(cancellation.Token);
    }

    private void StopRefreshAnimation()
    {
        _refreshAnimationCancellation?.Cancel();
        _refreshAnimationCancellation?.Dispose();
        _refreshAnimationCancellation = null;
        RefreshIcon.CancelAnimations();
        _ = RefreshIcon.RotateToAsync(0, 180, Easing.CubicOut);
    }

    private async Task AnimateRefreshIconAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RefreshIcon.RotateToAsync(RefreshIcon.Rotation + 360, 700, Easing.Linear);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}

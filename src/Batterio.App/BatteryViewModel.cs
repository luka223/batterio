using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Batterio.Core;

namespace Batterio.App;

public sealed class BatteryViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PollingCoordinator _polling;
    private string? _startupError;
    private bool _isRefreshing;

    public BatteryViewModel(PollingCoordinator polling)
    {
        _polling = polling;
        Devices = new ObservableCollection<DeviceStateViewModel>();
        RefreshAllCommand = new Command(async () => await RefreshAllAsync());
        _polling.StateChanged += OnStateChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DeviceStateViewModel> Devices { get; }

    public ICommand RefreshAllCommand { get; }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (_isRefreshing == value)
            {
                return;
            }

            _isRefreshing = value;
            OnPropertyChanged();
            ((Command)RefreshAllCommand).ChangeCanExecute();
        }
    }

    public string? StartupError
    {
        get => _startupError;
        private set
        {
            if (_startupError == value)
            {
                return;
            }

            _startupError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasStartupError));
        }
    }

    public bool HasStartupError => !string.IsNullOrWhiteSpace(StartupError);

    public bool HasNoDevices => Devices.Count == 0;

    public void SetStartupError(string message)
    {
        MainThread.BeginInvokeOnMainThread(() => StartupError = message);
    }

    public void Dispose()
    {
        _polling.StateChanged -= OnStateChanged;
    }

    private async Task RefreshAllAsync()
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            await _polling.RefreshAllAsync();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void OnStateChanged(object? sender, DeviceBatteryStateChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var item = Devices.FirstOrDefault(device => string.Equals(device.Id, e.State.Configuration.Id, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                Devices.Add(new DeviceStateViewModel(e.State));
            }
            else
            {
                item.Update(e.State);
            }

            OnPropertyChanged(nameof(HasNoDevices));
        });
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class DeviceStateViewModel : INotifyPropertyChanged
{
    private DeviceBatteryState _state;

    public DeviceStateViewModel(DeviceBatteryState state)
    {
        _state = state;
        BatterySegments = CreateBatterySegments(state.LastValidPercentage);
        RefreshCommand = new Command(async () => await RefreshAsync());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand RefreshCommand { get; }

    public string Id => _state.Configuration.Id;

    public string Name => _state.Configuration.Name;

    public string PhotoSource
    {
        get
        {
            try
            {
                var path = _state.Configuration.ResolvePhotoPath(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Batterio"));
                if (path is not null && File.Exists(path))
                {
                    return path;
                }

                return GetFallbackImageSource();
            }
            catch (Exception)
            {
                return GetFallbackImageSource();
            }
        }
    }

    private string GetFallbackImageSource() => _state.Configuration.Id switch
    {
        "logitech-g-pro-x2-superstrike" => "mouse_generated.png",
        "keychron-q1-he" => "keyboard_generated.png",
        "logitech-g-pro-x2-lightspeed" => "lightspeed.webp",
        _ => "battery_placeholder.scale-100.png"
    };

    public string StatusText => _state.StatusText;

    public string DetailText => _state.Error ?? _state.RetrievalState switch
    {
        BatteryRetrievalState.Ready => FormatLastUpdate(_state.LastSuccessfulUpdate),
        BatteryRetrievalState.Stale => $"Last successful update: {FormatLastUpdate(_state.LastSuccessfulUpdate)}",
        BatteryRetrievalState.Disabled => "Device is disabled.",
        BatteryRetrievalState.ConfigurationError => "Check the device adapter implementation.",
        _ => "No successful reading yet."
    };

    public Color BatteryColor => _state.LastValidPercentage switch
    {
        > 40 => Color.FromArgb("#22C55E"),
        >= 20 => Color.FromArgb("#EAB308"),
        _ => Color.FromArgb("#EF4444")
    };

    public IReadOnlyList<BatterySegmentViewModel> BatterySegments { get; private set; }

    public string BatteryDescription => $"{Name}: {StatusText}";

    public bool HasReading => _state.LastValidPercentage.HasValue;

    public void Update(DeviceBatteryState state)
    {
        _state = state;
        BatterySegments = CreateBatterySegments(state.LastValidPercentage);
        OnPropertyChanged(string.Empty);
    }

    private BatterySegmentViewModel[] CreateBatterySegments(int? percentage)
    {
        var filledSegments = Math.Clamp(percentage.GetValueOrDefault() / 10, 0, 10);
        return Enumerable.Range(0, 10)
            .Select(index => new BatterySegmentViewModel(index < filledSegments, BatteryColor))
            .ToArray();
    }

    private async Task RefreshAsync()
    {
        if (Application.Current?.Handler?.MauiContext?.Services.GetService(typeof(PollingCoordinator)) is PollingCoordinator polling)
        {
            await polling.RefreshDeviceAsync(Id);
        }
    }

    private static string FormatLastUpdate(DateTimeOffset? value) =>
        value is null ? "No successful reading yet." : value.Value.ToLocalTime().ToString("g");

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record BatterySegmentViewModel(bool IsFilled, Color Color);




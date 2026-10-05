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
    private long _lastSnapshotRevision;

    public BatteryViewModel(PollingCoordinator polling)
    {
        _polling = polling;
        Devices = new ObservableCollection<DeviceStateViewModel>();
        RefreshAllCommand = new Command(async () => await RefreshAllAsync());
        _polling.SnapshotChanged += OnSnapshotChanged;
        var current = _polling.CurrentSnapshot;
        if (current.Revision > 0)
        {
            ApplySnapshot(current);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? DevicesChanged;

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
        _polling.SnapshotChanged -= OnSnapshotChanged;
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

    private void OnSnapshotChanged(object? sender, PollingSnapshotChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() => ApplySnapshot(e.Snapshot));
    }

    private void ApplySnapshot(PollingSnapshot snapshot)
    {
        if (snapshot.Revision <= _lastSnapshotRevision)
        {
            return;
        }

        _lastSnapshotRevision = snapshot.Revision;
        var previousIds = Devices.Select(device => device.Id).ToArray();
        var statesById = snapshot.States.ToDictionary(
            state => state.Configuration.Id,
            StringComparer.OrdinalIgnoreCase);

        for (var index = Devices.Count - 1; index >= 0; index--)
        {
            if (!statesById.ContainsKey(Devices[index].Id))
            {
                Devices.RemoveAt(index);
            }
        }

        for (var index = 0; index < snapshot.States.Count; index++)
        {
            var state = snapshot.States[index];
            var existingIndex = -1;
            for (var currentIndex = 0; currentIndex < Devices.Count; currentIndex++)
            {
                if (string.Equals(Devices[currentIndex].Id, state.Configuration.Id, StringComparison.OrdinalIgnoreCase))
                {
                    existingIndex = currentIndex;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                Devices.Insert(Math.Min(index, Devices.Count), new DeviceStateViewModel(state));
            }
            else
            {
                Devices[existingIndex].Update(state);
                if (existingIndex != index)
                {
                    Devices.Move(existingIndex, Math.Min(index, Devices.Count - 1));
                }
            }
        }

        OnPropertyChanged(nameof(HasNoDevices));
        var currentIds = Devices.Select(device => device.Id).ToArray();
        if (!previousIds.SequenceEqual(currentIds, StringComparer.OrdinalIgnoreCase))
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class DeviceStateViewModel : INotifyPropertyChanged
{
    private DeviceBatteryState _state;
    private BatteryPresentation _presentation;

    public DeviceStateViewModel(DeviceBatteryState state)
    {
        _state = state;
        _presentation = BatteryPresentationRules.Create(state.Reading);
        BatterySegments = CreateBatterySegments();
        RefreshCommand = new Command(async () => await RefreshAsync());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand RefreshCommand { get; }

    public string Id => _state.Configuration.Id;

    public string Name => _state.Configuration.Name;

    public string PhotoSource => BatteryImageHelper.ResolveSource(_state.Configuration, IsOffline);

    public string PercentageText => _presentation.PercentageText;

    public bool IsPercentageVisible => _presentation.IsPercentageVisible;

    public bool IsCharging => _presentation.IsCharging;

    public bool IsOffline => _presentation.IsOffline;

    public bool IsNotOffline => !IsOffline;

    public Color BatteryColor => Color.FromArgb(_presentation.BatteryColorHex);

    public Color ChargingColor => Color.FromArgb(BatteryPresentationRules.ChargingColorHex);

    public Color ChargingBoltColor => Color.FromArgb("#14532D");

    public IReadOnlyList<BatterySegmentViewModel> BatterySegments { get; private set; }

    public string BatteryDescription =>
        BatteryPresentationRules.GetAccessibilityDescription(Name, _state.Reading);

    public void Update(DeviceBatteryState state)
    {
        _state = state;
        _presentation = BatteryPresentationRules.Create(state.Reading);
        BatterySegments = CreateBatterySegments();
        OnPropertyChanged(string.Empty);
    }

    private BatterySegmentViewModel[] CreateBatterySegments() =>
        Enumerable.Range(0, 10)
            .Select(index => new BatterySegmentViewModel(
                index < _presentation.FilledSegments,
                BatteryColor))
            .ToArray();

    private async Task RefreshAsync()
    {
        if (Application.Current?.Handler?.MauiContext?.Services.GetService(typeof(PollingCoordinator)) is PollingCoordinator polling)
        {
            await polling.RefreshDeviceAsync(Id);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record BatterySegmentViewModel(bool IsFilled, Color Color);

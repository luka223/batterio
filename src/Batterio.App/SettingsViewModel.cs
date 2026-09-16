using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Batterio.Core;
using Batterio.Core.Abstractions;

namespace Batterio.App;

public sealed class SettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly DeviceSelectionCoordinator _selection;
    private readonly IStartupRegistrationService _startup;
    private bool _isLoading = true;
    private bool _isStartupKnown;
    private bool _isStartupRegistered;
    private bool _isStartupPending;
    private string? _startupError;
    private string? _error;
    private int _disposed;

    public SettingsViewModel(
        DeviceSelectionCoordinator selection,
        IStartupRegistrationService startup)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
        Devices = new ObservableCollection<DeviceSelectionItemViewModel>();
        _selection.SnapshotChanged += OnSelectionSnapshotChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DeviceSelectionItemViewModel> Devices { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    public bool HasDevices => Devices.Count > 0;

    public bool HasNoDevices => !IsLoading && !HasDevices;

    public bool IsStartupKnown
    {
        get => _isStartupKnown;
        private set
        {
            if (SetField(ref _isStartupKnown, value))
            {
                OnPropertyChanged(nameof(IsStartupAvailable));
                OnPropertyChanged(nameof(IsStartupUnavailable));
            }
        }
    }

    public bool IsStartupAvailable => IsStartupKnown && !IsStartupPending;

    public bool IsStartupUnavailable => !IsStartupKnown;

    public bool IsStartupRegistered
    {
        get => _isStartupRegistered;
        private set => SetField(ref _isStartupRegistered, value);
    }

    public bool IsStartupPending
    {
        get => _isStartupPending;
        private set
        {
            if (SetField(ref _isStartupPending, value))
            {
                OnPropertyChanged(nameof(IsStartupAvailable));
            }
        }
    }

    public string? StartupError
    {
        get => _startupError;
        private set
        {
            if (SetField(ref _startupError, value))
            {
                OnPropertyChanged(nameof(HasStartupError));
            }
        }
    }

    public bool HasStartupError => !string.IsNullOrWhiteSpace(StartupError);

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    internal bool IsApplyingSnapshot { get; private set; }

    internal bool IsApplyingStartup { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        IsLoading = true;
        Error = null;
        var discoveryTask = InitializeDiscoveryAsync(cancellationToken);
        var startupTask = ReadStartupAsync(cancellationToken);
        await Task.WhenAll(discoveryTask, startupTask).ConfigureAwait(false);
        IsLoading = false;
    }

    public async Task RefreshOnActivationAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await ReadStartupAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _selection.RefreshDiscoveryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = exception.Message;
        }
    }

    public async Task SetStartupRegisteredAsync(bool registered, CancellationToken cancellationToken = default)
    {
        if (IsStartupPending)
        {
            return;
        }

        IsStartupPending = true;
        StartupError = null;
        try
        {
            await _startup.SetRegisteredAsync(registered, cancellationToken).ConfigureAwait(false);
            await ReadStartupAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var operationError = exception.Message;
            try
            {
                await ReadStartupAsync(cancellationToken).ConfigureAwait(false);
                StartupError = operationError;
            }
            catch (Exception readException) when (readException is not OperationCanceledException)
            {
                IsStartupKnown = false;
                StartupError = readException.Message;
            }
        }
        finally
        {
            IsStartupPending = false;
        }
    }

    public async Task SetDeviceSelectedAsync(
        DeviceSelectionItemViewModel item,
        bool selected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (IsApplyingSnapshot || item.IsBusy)
        {
            return;
        }

        var previous = item.CommittedSelection;
        item.IsSelected = selected;
        item.IsBusy = true;
        item.SetActionError(null);
        try
        {
            await _selection.SetDeviceSelectedAsync(item.DeviceId, selected, cancellationToken).ConfigureAwait(false);
            item.CommitSelection(selected);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            item.IsSelected = previous;
            item.SetActionError(exception.Message);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _selection.SnapshotChanged -= OnSelectionSnapshotChanged;
        }
    }

    private async Task InitializeDiscoveryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _selection.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = exception.Message;
        }
    }

    private async Task ReadStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            IsStartupKnown = false;
            IsApplyingStartup = true;
            IsStartupRegistered = await _startup.IsRegisteredAsync(cancellationToken).ConfigureAwait(false);
            IsStartupKnown = true;
            StartupError = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            IsStartupKnown = false;
            StartupError = exception.Message;
        }
        finally
        {
            IsApplyingStartup = false;
        }
    }

    private void OnSelectionSnapshotChanged(object? sender, DeviceSelectionSnapshotChangedEventArgs args)
    {
        MainThread.BeginInvokeOnMainThread(() => ApplySnapshot(args.Snapshot));
    }

    private void ApplySnapshot(DeviceSelectionSnapshot snapshot)
    {
        IsApplyingSnapshot = true;
        try
        {
            IsLoading = snapshot.IsLoading;
            Error = snapshot.Error;
            var byId = Devices.ToDictionary(item => item.DeviceId, StringComparer.OrdinalIgnoreCase);
            for (var index = Devices.Count - 1; index >= 0; index--)
            {
                if (!snapshot.Devices.Any(device => string.Equals(device.DeviceId, Devices[index].DeviceId, StringComparison.OrdinalIgnoreCase)))
                {
                    Devices.RemoveAt(index);
                }
            }

            for (var index = 0; index < snapshot.Devices.Count; index++)
            {
                var state = snapshot.Devices[index];
                if (!byId.TryGetValue(state.DeviceId, out var item))
                {
                    item = new DeviceSelectionItemViewModel(state);
                    Devices.Insert(Math.Min(index, Devices.Count), item);
                }
                else
                {
                    item.Update(state);
                    var currentIndex = Devices.IndexOf(item);
                    if (currentIndex != index)
                    {
                        Devices.Move(currentIndex, Math.Min(index, Devices.Count - 1));
                    }
                }
            }

            OnPropertyChanged(nameof(HasDevices));
            OnPropertyChanged(nameof(HasNoDevices));
        }
        finally
        {
            IsApplyingSnapshot = false;
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class DeviceSelectionItemViewModel : INotifyPropertyChanged
{
    private DeviceSelectionState _state;
    private bool _isSelected;
    private bool _isBusy;
    private string? _actionError;
    private bool _committedSelection;

    public DeviceSelectionItemViewModel(DeviceSelectionState state)
    {
        _state = state;
        _isSelected = state.IsSelected;
        _committedSelection = state.IsSelected;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DeviceId => _state.DeviceId;

    public string Name => _state.Name;

    public bool IsPresent => _state.IsPresent;

    public bool IsDisconnected => !IsPresent;

    public string ConnectionText => IsPresent ? "Connected" : "Disconnected";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    internal bool CommittedSelection => _committedSelection;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy != value)
            {
                _isBusy = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsEnabled));
            }
        }
    }

    public bool IsEnabled => !IsBusy;

    public string? Error => _actionError ?? _state.Error;

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public void Update(DeviceSelectionState state)
    {
        _state = state;
        _committedSelection = state.IsSelected;
        IsSelected = state.IsSelected;
        OnPropertyChanged(string.Empty);
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
    }

    public void SetActionError(string? error)
    {
        _actionError = error;
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
    }

    public void CommitSelection(bool selected)
    {
        _committedSelection = selected;
        IsSelected = selected;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

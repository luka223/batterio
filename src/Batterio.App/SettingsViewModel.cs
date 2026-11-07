using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Batterio.Core;
using Batterio.Core.Abstractions;
using Batterio.Core.Updates;
using Batterio.App.Windows;

namespace Batterio.App;

public sealed class SettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly DeviceSelectionCoordinator _selection;
    private readonly IStartupRegistrationService _startup;
    private readonly UpdateCoordinator _updates;
    private UpdateSnapshot _update;
    private bool _isLoading = true;
    private bool _isStartupKnown;
    private bool _isStartupRegistered;
    private bool _isStartupPending;
    private string? _startupError;
    private string? _error;
    private int _disposed;

    public SettingsViewModel(
        DeviceSelectionCoordinator selection,
        IStartupRegistrationService startup,
        UpdateCoordinator updates)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        Devices = new ObservableCollection<DeviceSelectionItemViewModel>();
        _selection.SnapshotChanged += OnSelectionSnapshotChanged;
        _update = _updates.Current;
        _updates.SnapshotChanged += OnUpdateSnapshotChanged;
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

    public bool CanCheckForUpdates => _update.Status is UpdateStatus.Idle or UpdateStatus.UpToDate or UpdateStatus.Available;

    public bool IsCheckingForUpdates => _update.Status == UpdateStatus.Checking;

    public bool IsUpToDate => _update.Status == UpdateStatus.UpToDate;

    public bool IsUpdateAvailable => _update.Status is UpdateStatus.Available or UpdateStatus.Downloading;

    /// <summary>The Download button stays visible, disabled, while the download runs.</summary>
    public bool ShowDownloadButton => _update.CanInstall && IsUpdateAvailable;

    public bool CanDownloadUpdate => _update.CanInstall && _update.Status == UpdateStatus.Available;

    /// <summary>The folder build and debug runs cannot replace one file; they link to the releases page.</summary>
    public bool ShowReleasesPageButton => !_update.CanInstall && _update.Status == UpdateStatus.Available;

    public bool IsDownloadingUpdate => _update.Status == UpdateStatus.Downloading;

    public double UpdateDownloadProgress => _update.Progress;

    public bool ShowInstallButton => _update.Status is UpdateStatus.ReadyToInstall or UpdateStatus.Installing;

    public bool CanInstallUpdate => _update.Status == UpdateStatus.ReadyToInstall;

    public string InstallButtonText => _update.Status == UpdateStatus.Installing ? "Updating..." : "Update now";

    public string UpdateStatusText => _update.Status switch
    {
        UpdateStatus.Checking => "Checking for updates...",
        UpdateStatus.UpToDate => $"Batterio is up to date ({FormatVersion(_update.CurrentVersion)}).",
        UpdateStatus.Available => $"Batterio {FormatVersion(_update.Release?.Version)} is available. You have {FormatVersion(_update.CurrentVersion)}.",
        UpdateStatus.Downloading => $"Downloading Batterio {FormatVersion(_update.Release?.Version)}... {(int)Math.Floor(_update.Progress * 100)}%",
        UpdateStatus.ReadyToInstall => $"Batterio {FormatVersion(_update.Release?.Version)} is ready. Batterio will restart to finish the update.",
        UpdateStatus.Installing => "Restarting into the new version...",
        _ => "Check GitHub for a newer version of Batterio."
    };

    public bool HasUpdateStatusText => _update.Status != UpdateStatus.UpToDate;

    public string? UpdateError => _update.Error;

    public bool HasUpdateError => !string.IsNullOrWhiteSpace(_update.Error);

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

    public async Task SetDeviceOnTaskbarAsync(
        DeviceSelectionItemViewModel item,
        bool onTaskbar,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (IsApplyingSnapshot || item.IsBusy)
        {
            return;
        }

        var previous = item.CommittedOnTaskbar;
        item.IsOnTaskbar = onTaskbar;
        item.IsBusy = true;
        item.SetActionError(null);
        try
        {
            await _selection.SetDeviceOnTaskbarAsync(item.DeviceId, onTaskbar, cancellationToken).ConfigureAwait(false);
            item.CommitOnTaskbar(onTaskbar);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            item.IsOnTaskbar = previous;
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
            _updates.SnapshotChanged -= OnUpdateSnapshotChanged;
        }
    }

    public Task CheckForUpdatesAsync() => RunUpdateActionAsync(() => _updates.CheckAsync());

    public Task DownloadUpdateAsync() => RunUpdateActionAsync(() => _updates.DownloadAsync());

    public Task InstallUpdateAsync() => RunUpdateActionAsync(_updates.InstallAsync);

    public static Task OpenReleasesPageAsync() =>
        Launcher.Default.OpenAsync(new Uri(AppUpdateEnvironment.ReleasesPage));

    private static async Task RunUpdateActionAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnUpdateSnapshotChanged(object? sender, UpdateSnapshot snapshot)
    {
        MainThread.BeginInvokeOnMainThread(() => ApplyUpdateSnapshot(snapshot));
    }

    private void ApplyUpdateSnapshot(UpdateSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        // Events are marshalled one by one, so a late progress event must not undo a newer state.
        _update = _updates.Current;
        foreach (var name in UpdatePropertyNames)
        {
            OnPropertyChanged(name);
        }
    }

    private static readonly string[] UpdatePropertyNames =
    [
        nameof(CanCheckForUpdates), nameof(IsCheckingForUpdates), nameof(IsUpToDate), nameof(IsUpdateAvailable),
        nameof(ShowDownloadButton), nameof(CanDownloadUpdate), nameof(ShowReleasesPageButton),
        nameof(IsDownloadingUpdate), nameof(UpdateDownloadProgress), nameof(ShowInstallButton),
        nameof(CanInstallUpdate), nameof(InstallButtonText), nameof(UpdateStatusText), nameof(HasUpdateStatusText),
        nameof(UpdateError), nameof(HasUpdateError)
    ];

    private static string FormatVersion(Version? version) => version?.ToString(3) ?? "unknown";

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
    private bool _isOnTaskbar;
    private bool _committedOnTaskbar;

    public DeviceSelectionItemViewModel(DeviceSelectionState state)
    {
        _state = state;
        _isSelected = state.IsSelected;
        _committedSelection = state.IsSelected;
        _isOnTaskbar = state.IsOnTaskbar;
        _committedOnTaskbar = state.IsOnTaskbar;
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
                OnPropertyChanged(nameof(IsTaskbarEnabled));
            }
        }
    }

    internal bool CommittedSelection => _committedSelection;

    public bool IsOnTaskbar
    {
        get => _isOnTaskbar;
        set
        {
            if (_isOnTaskbar != value)
            {
                _isOnTaskbar = value;
                OnPropertyChanged();
            }
        }
    }

    internal bool CommittedOnTaskbar => _committedOnTaskbar;

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
                OnPropertyChanged(nameof(IsTaskbarEnabled));
            }
        }
    }

    public bool IsEnabled => !IsBusy;

    // The taskbar option only matters for a device that is shown in the popup (and so
    // polled). Its stored value is kept while the device is switched off.
    public bool IsTaskbarEnabled => !IsBusy && IsSelected;

    public string? Error => _actionError ?? _state.Error;

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public void Update(DeviceSelectionState state)
    {
        _state = state;
        _committedSelection = state.IsSelected;
        _committedOnTaskbar = state.IsOnTaskbar;
        IsSelected = state.IsSelected;
        IsOnTaskbar = state.IsOnTaskbar;
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

    public void CommitOnTaskbar(bool onTaskbar)
    {
        _committedOnTaskbar = onTaskbar;
        IsOnTaskbar = onTaskbar;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

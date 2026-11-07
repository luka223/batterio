using Batterio.Core.Abstractions;

namespace Batterio.Core.Updates;

public enum UpdateStatus
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToInstall,
    Installing
}

public sealed record UpdateSnapshot(
    UpdateStatus Status,
    Version CurrentVersion,
    UpdateRelease? Release,
    double Progress,
    string? Error,
    bool CanInstall);

/// <summary>Starts the replaced executable and shuts the running copy down.</summary>
public interface IUpdateRestarter
{
    /// <summary>Starts <paramref name="executablePath"/>; throws when it could not be started.</summary>
    void Start(string executablePath);

    /// <summary>Exits the running copy once the new one has been started.</summary>
    Task ExitAsync();
}

/// <summary>
/// Owns the update state for the whole process, so a download keeps running and keeps its
/// progress when the Settings window is closed and reopened.
/// </summary>
public sealed class UpdateCoordinator
{
    private readonly IUpdateReleaseSource _source;
    private readonly UpdateDownloader _downloader;
    private readonly IUpdateRestarter _restarter;
    private readonly IBatterioLog _log;
    private readonly string? _executablePath;
    private readonly object _gate = new();
    private UpdateSnapshot _current;
    private bool _cleanedUp;

    public UpdateCoordinator(
        IUpdateReleaseSource source,
        UpdateDownloader downloader,
        IUpdateRestarter restarter,
        Version currentVersion,
        string? executablePath,
        IBatterioLog log)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _restarter = restarter ?? throw new ArgumentNullException(nameof(restarter));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _executablePath = string.IsNullOrWhiteSpace(executablePath) ? null : Path.GetFullPath(executablePath);
        _current = new UpdateSnapshot(UpdateStatus.Idle, currentVersion, null, 0, null, _executablePath is not null);
    }

    public event EventHandler<UpdateSnapshot>? SnapshotChanged;

    public UpdateSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Checks GitHub for a newer release. Does nothing while a download or install is under way.</summary>
    public async Task<UpdateSnapshot> CheckAsync(CancellationToken cancellationToken = default)
    {
        UpdateSnapshot before;
        lock (_gate)
        {
            before = _current;
            if (before.Status is UpdateStatus.Checking or UpdateStatus.Downloading or UpdateStatus.ReadyToInstall or UpdateStatus.Installing)
            {
                return before;
            }

            _current = before with { Status = UpdateStatus.Checking, Error = null };
        }

        Publish();
        CleanUpOnce();
        try
        {
            var release = await _source.GetLatestAsync(cancellationToken).ConfigureAwait(false);
            var available = release is not null && release.Version > before.CurrentVersion;
            _log.Log(BatterioLogLevel.Information, available
                ? $"Update check found version {release!.Version}."
                : "Update check found no newer version.");
            return Set(current => current with
            {
                Status = available ? UpdateStatus.Available : UpdateStatus.UpToDate,
                Release = available ? release : null,
                Progress = 0,
                Error = null
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _log.Log(BatterioLogLevel.Warning, "Update check failed.", exception);
            return Set(current => current with { Status = before.Status, Error = Describe(exception) });
        }
        catch (OperationCanceledException)
        {
            Set(current => current with { Status = before.Status });
            throw;
        }
    }

    /// <summary>Downloads the available release next to the installed executable.</summary>
    public async Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        UpdateRelease release;
        string executable;
        lock (_gate)
        {
            if (_current.Status != UpdateStatus.Available || _current.Release is null || _executablePath is null)
            {
                return;
            }

            release = _current.Release;
            executable = _executablePath;
            _current = _current with { Status = UpdateStatus.Downloading, Progress = 0, Error = null };
        }

        Publish();
        var progress = new SynchronousProgress(OnProgress);
        try
        {
            await _downloader.DownloadAsync(release, UpdateFiles.DownloadPathFor(executable), progress, cancellationToken)
                .ConfigureAwait(false);
            _log.Log(BatterioLogLevel.Information, $"Downloaded update {release.Version}.");
            Set(current => current with { Status = UpdateStatus.ReadyToInstall, Progress = 1, Error = null });
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Warning, "Update download failed.", exception);
            Set(current => current with
            {
                Status = UpdateStatus.Available,
                Progress = 0,
                Error = exception is OperationCanceledException ? null : Describe(exception)
            });
            if (exception is OperationCanceledException)
            {
                throw;
            }
        }
    }

    /// <summary>Replaces the installed executable with the download and restarts into it.</summary>
    public async Task InstallAsync()
    {
        string executable;
        lock (_gate)
        {
            if (_current.Status != UpdateStatus.ReadyToInstall || _executablePath is null)
            {
                return;
            }

            executable = _executablePath;
            _current = _current with { Status = UpdateStatus.Installing, Error = null };
        }

        Publish();
        var download = UpdateFiles.DownloadPathFor(executable);
        try
        {
            UpdateFiles.Swap(executable, download);
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Warning, "Could not replace the installed executable.", exception);
            var lost = !File.Exists(download);
            Set(current => current with
            {
                Status = lost ? UpdateStatus.Available : UpdateStatus.ReadyToInstall,
                Progress = lost ? 0 : current.Progress,
                Error = Describe(exception)
            });
            return;
        }

        try
        {
            _restarter.Start(executable);
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Error, "The updated executable could not be started; restoring the previous one.", exception);
            try
            {
                UpdateFiles.Revert(executable, download);
            }
            catch (Exception revertException)
            {
                _log.Log(BatterioLogLevel.Error, "Restoring the previous executable failed.", revertException);
            }

            Set(current => current with
            {
                Status = File.Exists(download) ? UpdateStatus.ReadyToInstall : UpdateStatus.Available,
                Error = "The new version could not be started. The previous version was kept."
            });
            return;
        }

        _log.Log(BatterioLogLevel.Information, $"Restarting into update {Current.Release?.Version}.");
        await _restarter.ExitAsync().ConfigureAwait(false);
    }

    private void CleanUpOnce()
    {
        string? executable;
        lock (_gate)
        {
            if (_cleanedUp)
            {
                return;
            }

            _cleanedUp = true;
            executable = _executablePath;
        }

        if (executable is not null)
        {
            // A download from an earlier session is not trusted again; the next download replaces it.
            UpdateFiles.CleanUp(executable, includeDownload: true);
        }
    }

    private void OnProgress(double value)
    {
        lock (_gate)
        {
            if (_current.Status != UpdateStatus.Downloading ||
                (Math.Floor(value * 100) <= Math.Floor(_current.Progress * 100) && value < 1))
            {
                return;
            }

            _current = _current with { Progress = value };
        }

        Publish();
    }

    private UpdateSnapshot Set(Func<UpdateSnapshot, UpdateSnapshot> change)
    {
        UpdateSnapshot snapshot;
        lock (_gate)
        {
            snapshot = _current = change(_current);
        }

        Publish();
        return snapshot;
    }

    private void Publish()
    {
        var snapshot = Current;
        try
        {
            SnapshotChanged?.Invoke(this, snapshot);
        }
        catch (Exception exception)
        {
            _log.Log(BatterioLogLevel.Warning, "An update state listener failed.", exception);
        }
    }

    private string Describe(Exception exception) => exception switch
    {
        UpdateException => exception.Message,
        HttpRequestException => "Could not reach GitHub. Check your internet connection and try again.",
        TaskCanceledException or OperationCanceledException => "GitHub did not respond in time. Try again.",
        UnauthorizedAccessException or IOException =>
            $"Batterio cannot write to {Path.GetDirectoryName(_executablePath) ?? "its folder"}. " +
            "Move Batterio.exe to a folder you can write to, or download the update from the releases page.",
        _ => "The update failed. Try again, or download it from the releases page."
    };

    /// <summary>Reports on the calling thread, unlike <see cref="Progress{T}"/>.</summary>
    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}

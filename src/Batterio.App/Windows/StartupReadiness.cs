using System.Text.RegularExpressions;
using Batterio.Core.Startup;

namespace Batterio.App.Windows;

/// <summary>
/// Owns the optional launcher handshake event for this runtime process.
/// </summary>
public sealed class StartupReadiness : IDisposable
{
    private static readonly Regex EventNameFormat = new(
        "^Batterio\\.StartupReady\\.[0-9a-fA-F]{32}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly EventWaitHandle? _event;
    private readonly bool _configured;
    private readonly bool _valid;
    private int _signaled;
    private int _disposed;

    private StartupReadiness(string? eventName, Func<string, EventWaitHandle>? openExisting = null)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return;
        }

        _configured = true;
        if (!EventNameFormat.IsMatch(eventName))
        {
            return;
        }

        try
        {
            _event = (openExisting ?? EventWaitHandle.OpenExisting)(eventName);
            _valid = true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // A configured but unavailable event is a supervised failure. The app must
            // not claim readiness when the launcher cannot observe it.
        }
        catch (UnauthorizedAccessException)
        {
            // Treat an inaccessible event the same way without exposing its name in logs.
        }
    }

    /// <summary>Whether the process was started by a launcher that expects a handshake.</summary>
    public bool IsSupervised => _configured;

    /// <summary>Whether the configured handshake event was opened successfully.</summary>
    public bool HasValidEvent => _valid;

    /// <summary>Whether a handshake variable was supplied, even if it was malformed.</summary>
    public bool IsConfigured => _configured;

    public static StartupReadiness? Current { get; private set; }

    public static StartupReadiness CaptureFromEnvironment()
    {
        var name = Environment.GetEnvironmentVariable(StartupConstants.StartupReadyEventEnvironmentVariable);
        Environment.SetEnvironmentVariable(StartupConstants.StartupReadyEventEnvironmentVariable, null);
        var readiness = new StartupReadiness(name);
        Current = readiness;
        return readiness;
    }

    internal static StartupReadiness CreateForTesting(
        string? eventName,
        Func<string, EventWaitHandle> openExisting) =>
        new(eventName, openExisting ?? throw new ArgumentNullException(nameof(openExisting)));

    /// <summary>
    /// Signals exactly once. Direct developer launches without the variable are valid and
    /// return true because there is no external supervisor to notify.
    /// </summary>
    public bool Signal()
    {
        if (!_configured)
        {
            return true;
        }

        if (!_valid || _event is null || Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        if (Interlocked.Exchange(ref _signaled, 1) != 0)
        {
            return true;
        }

        try
        {
            _event.Set();
            return true;
        }
        catch (ObjectDisposedException)
        {
            Interlocked.Exchange(ref _signaled, 0);
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _event?.Dispose();
        }
    }
}

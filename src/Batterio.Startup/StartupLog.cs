namespace Batterio.Startup;

/// <summary>Writes small, bounded records without persisting arguments or event names.</summary>
public sealed class StartupLog : IStartupLog
{
    private const int MaximumBytes = 128 * 1024;
    private const int LockRetryCount = 100;
    private const int LockRetryDelayMilliseconds = 20;
    private static readonly System.Text.UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly object Gate = new();
    private readonly string _path;
    private readonly TimeProvider _timeProvider;

    public StartupLog(string? path = null, TimeProvider? timeProvider = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Batterio", "Logs", "startup.log");
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void LauncherStarted(string version) => Append($"version={Safe(version)} event=launcher-start");

    public void ShellUnavailableAfterTimeout() => Append("event=shell-timeout action=launch-anyway");

    public void AttemptCompleted(int attempt, int? processId, int? exitCode, bool readinessSignaled, string? failureType) =>
        Append($"attempt={attempt} pid={processId?.ToString() ?? "none"} exit={exitCode?.ToString() ?? "none"} readiness={readinessSignaled} failure={Safe(failureType) ?? "none"}");

    public void FinalFailure(int attempts) => Append($"event=final-failure attempts={attempts}");

    public void RuntimeMissing() => Append("event=runtime-missing");

    public void UnexpectedFailure(string failureType) =>
        Append($"event=launcher-failure failure={Safe(failureType) ?? "unknown"}");

    private void Append(string message)
    {
        try
        {
            lock (Gate)
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var line = $"{_timeProvider.GetUtcNow():O} {message}{Environment.NewLine}";
                using var stream = OpenForUpdate();
                string existing;
                using (var reader = new StreamReader(
                           stream,
                           System.Text.Encoding.UTF8,
                           detectEncodingFromByteOrderMarks: true,
                           bufferSize: 1024,
                           leaveOpen: true))
                {
                    existing = reader.ReadToEnd();
                }

                var combined = existing + line;
                var bytes = Utf8WithoutBom.GetBytes(combined);
                if (bytes.Length > MaximumBytes)
                {
                    var start = bytes.Length - MaximumBytes;
                    while (start < bytes.Length && bytes[start] != (byte)'\n')
                    {
                        start++;
                    }

                    combined = Utf8WithoutBom.GetString(bytes, Math.Min(start + 1, bytes.Length),
                        Math.Max(0, bytes.Length - Math.Min(start + 1, bytes.Length)));
                }

                stream.Position = 0;
                stream.SetLength(0);
                using var writer = new StreamWriter(stream, Utf8WithoutBom, bufferSize: 1024, leaveOpen: true);
                writer.Write(combined);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
        }
        catch
        {
            // Startup diagnostics must never prevent the supervisor from recovering the app.
        }
    }

    private FileStream OpenForUpdate()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(
                    _path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.None);
            }
            catch (IOException) when (attempt < LockRetryCount)
            {
                Thread.Sleep(LockRetryDelayMilliseconds);
            }
        }
    }

    private static string? Safe(string? value) =>
        value is null ? null : value.Replace('\r', '_').Replace('\n', '_').Replace(' ', '_');
}

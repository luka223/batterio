using Batterio.Core.Abstractions;

namespace Batterio.Core;

public sealed class RotatingFileLog : IBatterioLog, IDisposable
{
    private const long MaxFileBytes = 512 * 1024;
    private const int MaxFiles = 3;
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _filePath;

    public RotatingFileLog(string directory)
    {
        _directory = Path.GetFullPath(directory);
        _filePath = Path.Combine(_directory, "batterio.log");
        Directory.CreateDirectory(_directory);
    }

    public void Log(BatterioLogLevel level, string message, Exception? exception = null)
    {
        var line = $"{DateTimeOffset.UtcNow:O} [{level}] {message}" +
                   (exception is null ? string.Empty : $" {exception.GetType().Name}: {exception.Message}") +
                   Environment.NewLine;
        lock (_gate)
        {
            try
            {
                RotateIfNeeded(line.Length);
                File.AppendAllText(_filePath, line);
            }
            catch
            {
                // Diagnostics must never bring down the tray process.
            }
        }
    }

    public void Dispose()
    {
    }

    private void RotateIfNeeded(int nextLineLength)
    {
        if (!File.Exists(_filePath) || new FileInfo(_filePath).Length + nextLineLength <= MaxFileBytes)
        {
            return;
        }

        var oldest = Path.Combine(_directory, $"batterio.{MaxFiles - 1}.log");
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = MaxFiles - 2; index >= 1; index--)
        {
            var source = Path.Combine(_directory, $"batterio.{index}.log");
            var destination = Path.Combine(_directory, $"batterio.{index + 1}.log");
            if (File.Exists(source))
            {
                File.Move(source, destination, true);
            }
        }

        File.Move(_filePath, Path.Combine(_directory, "batterio.1.log"), true);
    }
}

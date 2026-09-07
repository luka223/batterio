namespace Batterio.Core.Abstractions;

public enum BatterioLogLevel
{
    Trace,
    Information,
    Warning,
    Error,
    Critical
}

public interface IBatterioLog
{
    void Log(BatterioLogLevel level, string message, Exception? exception = null);
}

public sealed class NullBatterioLog : IBatterioLog
{
    public void Log(BatterioLogLevel level, string message, Exception? exception = null)
    {
    }
}

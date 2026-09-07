namespace Batterio.Core.Abstractions;

public interface IClickDelay
{
    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemClickDelay : IClickDelay
{
    public Task Delay(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

public interface IDoubleClickTimeProvider
{
    TimeSpan GetDoubleClickTime();
}

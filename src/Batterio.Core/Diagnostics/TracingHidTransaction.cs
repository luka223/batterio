using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>Non-owning transaction decorator; packet capture is explicitly opt-in.</summary>
internal sealed class TracingHidTransaction(IHidReportTransaction inner, IBatterioLog log, string path) : IHidReportTransaction
{
    private readonly bool _enabled = Environment.GetEnvironmentVariable("BATTERIO_TRACE_HID") == "1";

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default)
    {
        if (_enabled) log.Log(BatterioLogLevel.Trace, $"HID TX '{path}': {Convert.ToHexString(report.Span)}");
        await inner.WriteAsync(report, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> ReadAsync(Memory<byte> report, CancellationToken cancellationToken = default)
    {
        var count = await inner.ReadAsync(report, cancellationToken).ConfigureAwait(false);
        if (_enabled) log.Log(BatterioLogLevel.Trace, $"HID RX '{path}': {Convert.ToHexString(report.Span[..count])}");
        return count;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

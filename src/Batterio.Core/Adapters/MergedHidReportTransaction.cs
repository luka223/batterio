using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Writes through one HID collection and reads from several sibling collections of the same interface.
/// </summary>
/// <remarks>
/// Windows splits a Logitech receiver's HID++ interface into a short-report (0x10) and a long-report (0x11) collection.
/// A request written to the long collection can be answered on the short one: the receiver reports HID++ 1.0 errors
/// (for example "device not connected" for an empty or sleeping slot) as short reports. Reading only the long
/// collection would wait for a reply that never comes there.
/// </remarks>
internal sealed class MergedHidReportTransaction : IHidReportTransaction
{
    private readonly IHidReportTransaction _primary;
    private readonly Channel<byte[]> _reports = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task[] _pumps;
    private ExceptionDispatchInfo? _primaryFailure;
    private int _disposed;

    /// <param name="primary">Receives every write; its read failures end the transaction.</param>
    /// <param name="primaryInputLength">Input report length of the primary collection.</param>
    /// <param name="secondary">Collections that are only read; their failures are ignored.</param>
    public MergedHidReportTransaction(
        IHidReportTransaction primary,
        int primaryInputLength,
        IReadOnlyList<(IHidReportTransaction Transaction, int InputLength)> secondary)
    {
        _primary = primary;
        _pumps = new[] { Task.Run(() => PumpAsync(primary, primaryInputLength, isPrimary: true)) }
            .Concat(secondary.Select(entry => Task.Run(() => PumpAsync(entry.Transaction, entry.InputLength, isPrimary: false))))
            .ToArray();
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _primary.WriteAsync(report, cancellationToken);
    }

    public async ValueTask<int> ReadAsync(Memory<byte> report, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        byte[] received;
        try
        {
            received = await _reports.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            _primaryFailure?.Throw();
            return 0;
        }

        var count = Math.Min(received.Length, report.Length);
        received.AsSpan(0, count).CopyTo(report.Span);
        return count;
    }

    private async Task PumpAsync(IHidReportTransaction source, int inputLength, bool isPrimary)
    {
        var buffer = new byte[Math.Max(64, inputLength)];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var count = await source.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (count <= 0)
                {
                    if (isPrimary) _reports.Writer.TryComplete();
                    return;
                }

                _reports.Writer.TryWrite(buffer.AsSpan(0, count).ToArray());
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (isPrimary)
        {
            _primaryFailure = ExceptionDispatchInfo.Capture(exception);
            _reports.Writer.TryComplete();
        }
        catch (Exception)
        {
            // A secondary collection only adds replies; losing it falls back to waiting on the primary one.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try { await Task.WhenAll(_pumps).ConfigureAwait(false); }
        catch (Exception) { }
        _reports.Writer.TryComplete();
        _stop.Dispose();
    }
}

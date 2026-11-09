using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Reads the Arctis Nova Pro Omni through its base station (hub): writes the status request and waits for the <c>01 B0</c> reply.
/// The hub can need a few seconds after enumeration before it answers, so a request that gets no answer is retried once after a settle delay.
/// </summary>
public sealed class SteelSeriesHubRoute : IBatteryRoute
{
    public const int MaximumFramesPerQuery = 32;
    public static readonly TimeSpan DefaultReplyTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(3);

    private readonly IHidCollectionProvider _provider;
    private readonly IBatterioLog _log;
    private readonly TimeSpan _replyTimeout;
    private readonly TimeSpan _retryDelay;

    public SteelSeriesHubRoute(
        IHidCollectionProvider provider,
        IBatterioLog? log = null,
        TimeSpan? replyTimeout = null,
        TimeSpan? retryDelay = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _log = log ?? new NullBatterioLog();
        _replyTimeout = replyTimeout ?? DefaultReplyTimeout;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
    }

    public async Task<BatteryReading> ReadAsync(ResolvedRoute route, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        cancellationToken.ThrowIfCancellationRequested();
        var collection = route.Collection ?? throw new BatteryAdapterException(BatteryAdapterErrorKind.ReceiverMissing,
            $"USB receiver route '{route.EndpointPath}' has no HID collection.");

        try
        {
            return await QueryCollectionAsync(collection, cancellationToken).ConfigureAwait(false);
        }
        catch (BatteryAdapterException error) when (error.Kind == BatteryAdapterErrorKind.DeviceBusy)
        {
            // The hub can need a few seconds after enumeration before it answers; retry once after a settle delay.
            _log.Log(BatterioLogLevel.Information, "SteelSeries hub did not answer; retrying after settle delay.");
        }

        await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
        return await QueryCollectionAsync(collection, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BatteryReading> QueryCollectionAsync(HidCollectionInfo collection, CancellationToken cancellationToken)
    {
        await using var transport = await _provider.OpenAsync(collection, cancellationToken).ConfigureAwait(false);
        await using var rawTransaction = await transport.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var transaction = new TracingHidTransaction(rawTransaction, _log, collection.DevicePath);

        await transaction.WriteAsync(
            SteelSeriesNovaProOmniStatusDecoding.CreateStatusRequest(collection.OutputReportLength),
            cancellationToken).ConfigureAwait(false);

        var reading = SteelSeriesNovaProOmniStatusDecoding.Decode(
            await ReadStatusReplyAsync(transaction, collection, _replyTimeout, cancellationToken).ConfigureAwait(false));
        _log.Log(BatterioLogLevel.Information,
            $"Arctis Nova Pro Omni headset {Describe(reading)}, reserve {(reading.Reserve is null ? "empty" : Describe(reading.Reserve))}.");
        return reading;
    }

    private static async Task<byte[]> ReadStatusReplyAsync(
        TracingHidTransaction transaction,
        HidCollectionInfo collection,
        TimeSpan replyTimeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(replyTimeout);
        var buffer = new byte[Math.Max(collection.InputReportLength, SteelSeriesNovaProOmniStatusDecoding.MinimumReplyLength)];
        try
        {
            for (var frame = 0; frame < MaximumFramesPerQuery; frame++)
            {
                var count = await transaction.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                // Unsolicited 07 xx events and unrelated frames are skipped until the 01 B0 reply arrives.
                if (SteelSeriesNovaProOmniStatusDecoding.IsStatusReply(buffer.AsSpan(0, count)))
                {
                    return buffer.AsSpan(0, count).ToArray();
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.DeviceBusy,
                "The SteelSeries Arctis Nova Pro Omni hub did not answer the status request in time.");
        }

        throw new BatteryAdapterException(BatteryAdapterErrorKind.ProtocolError,
            $"The SteelSeries Arctis Nova Pro Omni hub sent {MaximumFramesPerQuery} frames without a status reply.");
    }

    private static string Describe(BatteryReading reading) =>
        reading.Percentage is int percentage ? $"{percentage}% ({reading.ChargeState})" : reading.ChargeState.ToString();
}

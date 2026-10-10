using System.Diagnostics;
using System.Text.Json;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

public sealed class SteelSeriesArctisNovaProOmniAdapter : IBatteryAdapter
{
    public const int MaximumFramesPerQuery = 32;
    public static readonly TimeSpan DefaultReplyTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(3);

    private readonly IHidCollectionProvider _provider;
    private readonly IBatterioLog _log;
    private readonly string? _devicePath;
    private readonly TimeSpan _replyTimeout;
    private readonly TimeSpan _retryDelay;
    private int _disposed;
    private readonly CancellationTokenSource _lifetime = new();

    public SteelSeriesArctisNovaProOmniAdapter()
        : this(new EmptyHidCollectionProvider(), null, null)
    {
    }

    public SteelSeriesArctisNovaProOmniAdapter(
        IHidCollectionProvider provider,
        IBatterioLog? log = null,
        string? devicePath = null,
        TimeSpan? replyTimeout = null,
        TimeSpan? retryDelay = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _log = log ?? new NullBatterioLog();
        _devicePath = devicePath;
        _replyTimeout = replyTimeout ?? DefaultReplyTimeout;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
    }

    public async Task<BatteryReading> GetBatteryStatus(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var started = Stopwatch.GetTimestamp();
        try { return await QueryAsync(attempt.Token).ConfigureAwait(false); }
        catch (BatteryAdapterException error)
        {
            _log.Log(BatterioLogLevel.Warning, $"SteelSeries query failed ({error.Kind}): {error.Message}");
            throw;
        }
        finally
        {
            _log.Log(BatterioLogLevel.Information, $"SteelSeries query finished in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
        }
    }

    private async Task<BatteryReading> QueryAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        var query = SteelSeriesDefinitions.NovaProOmniQuery;
        var collections = _provider.Enumerate(query)
            .Where(query.Matches)
            .Where(collection => _devicePath is null ||
                                 string.Equals(collection.DevicePath, _devicePath, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (collections.Length == 0)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.ReceiverMissing,
                _devicePath is null
                    ? "The SteelSeries Arctis Nova Pro Omni hub was not found (set the hub switch to USB-1)."
                    : $"The configured SteelSeries Arctis Nova Pro Omni hub path '{_devicePath}' was not found.");
        }

        if (_devicePath is null && collections.Length > 1)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.ReceiverAmbiguous,
                "More than one SteelSeries Arctis Nova Pro Omni status collection matched; configure devicePath to select one.");
        }

        var collection = collections[0];
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

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _lifetime.Cancel();
        return ValueTask.CompletedTask;
    }
}

public sealed class SteelSeriesArctisNovaProOmniAdapterFactory : IBatteryAdapterFactory
{
    private readonly IHidCollectionProvider _provider;
    private readonly IBatterioLog _log;

    public SteelSeriesArctisNovaProOmniAdapterFactory()
        : this(new EmptyHidCollectionProvider(), null)
    {
    }

    public SteelSeriesArctisNovaProOmniAdapterFactory(IHidCollectionProvider provider, IBatterioLog? log = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _log = log ?? new NullBatterioLog();
    }

    public string AdapterId => "steelseries-arctis-nova-pro-omni";

    public void ValidateSettings(JsonElement settings) => HidAdapterSettings.ReadDevicePath(settings, AdapterId);

    public IBatteryAdapter CreateAdapter(DeviceConfiguration device) =>
        new SteelSeriesArctisNovaProOmniAdapter(
            _provider,
            _log,
            HidAdapterSettings.ReadDevicePath(device.AdapterSettings, AdapterId));
}

using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Reads the Q1 HE battery from the report the custom firmware pushes through the Keychron Link dongle. It only listens and never writes.
/// When the <see cref="KeychronLinkMonitor"/> already listens to the dongle, the newest report it holds answers the read at once;
/// otherwise the route listens itself for up to <see cref="DefaultListenTimeout"/>.
/// </summary>
public sealed class KeychronLinkRoute : IBatteryRoute
{
    public const int MaximumFramesPerQuery = 64;
    public static readonly TimeSpan DefaultListenTimeout = TimeSpan.FromSeconds(4);

    /// <summary>A report this young is as good as a new one: the firmware pushes one about every two seconds.</summary>
    public static readonly TimeSpan CachedFrameMaxAge = TimeSpan.FromSeconds(4);

    private readonly IHidCollectionProvider _provider;
    private readonly IBatterioLog _log;
    private readonly TimeSpan _listenTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly KeychronLinkMonitor? _monitor;
    private readonly Lock _chargingGate = new();
    private double? _lastChargingPercentage;
    private DateTimeOffset _lastChargingAt;

    public KeychronLinkRoute(
        IHidCollectionProvider provider,
        IBatterioLog? log = null,
        TimeSpan? listenTimeout = null,
        TimeProvider? timeProvider = null,
        KeychronLinkMonitor? monitor = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _log = log ?? new NullBatterioLog();
        _listenTimeout = listenTimeout ?? DefaultListenTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _monitor = monitor;
    }

    public async Task<BatteryReading> ReadAsync(ResolvedRoute route, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        cancellationToken.ThrowIfCancellationRequested();
        var collection = route.Collection ?? throw new BatteryAdapterException(BatteryAdapterErrorKind.ReceiverMissing,
            $"USB receiver route '{route.EndpointPath}' has no HID collection.");

        var frame = await ObtainFrameAsync(collection, cancellationToken).ConfigureAwait(false);
        var reading = SmoothCharging(KeychronBatteryReportDecoding.Decode(frame, _log), frame);
        _log.Log(BatterioLogLevel.Information,
            $"Keychron Q1 HE {reading.Percentage}% ({reading.ChargeState}), firmware {frame[KeychronBatteryReportDecoding.PercentageOffset]}%, " +
            $"{KeychronBatteryReportDecoding.DecodeMillivolts(frame)} mV, charge byte 0x{frame[KeychronBatteryReportDecoding.ChargeOffset]:X2}, " +
            $"connection 0x{frame[KeychronBatteryReportDecoding.ConnectionOffset]:X2}.");
        return reading;
    }

    private async Task<byte[]> ObtainFrameAsync(HidCollectionInfo collection, CancellationToken cancellationToken)
    {
        if (_monitor is not null && _monitor.TryGetListener(collection.DevicePath, out var listener))
        {
            var cached = await listener.WaitForFrameAsync(CachedFrameMaxAge, _listenTimeout, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }

            if (!listener.IsClosed)
            {
                throw NoReport();
            }

            // The listener failed while we waited; listen directly instead.
        }

        await using var transport = await _provider.OpenAsync(collection, cancellationToken).ConfigureAwait(false);
        await using var rawTransaction = await transport.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var transaction = new TracingHidTransaction(rawTransaction, _log, collection.DevicePath);
        return await ListenForReportAsync(transaction, collection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// While charging, holds the estimate steady and rate-limited across queries; the state is dropped once charging stops.
    /// </summary>
    private BatteryReading SmoothCharging(BatteryReading reading, byte[] frame)
    {
        lock (_chargingGate)
        {
            if (reading.ChargeState != BatteryChargeState.Charging)
            {
                _lastChargingPercentage = null;
                return reading;
            }

            var now = _timeProvider.GetUtcNow();
            var estimate = KeychronBatteryReportDecoding.EstimateChargingPercentage(KeychronBatteryReportDecoding.DecodeMillivolts(frame));
            if (_lastChargingPercentage is { } previous)
            {
                estimate = KeychronBatteryReportDecoding.LimitChargingRise(estimate, previous, now - _lastChargingAt);
            }

            _lastChargingPercentage = estimate;
            _lastChargingAt = now;
            return new BatteryReading((int)estimate, reading.ChargeState);
        }
    }

    private async Task<byte[]> ListenForReportAsync(
        TracingHidTransaction transaction,
        HidCollectionInfo collection,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_listenTimeout);
        var buffer = new byte[Math.Max(collection.InputReportLength, KeychronBatteryReportDecoding.MinimumReportLength)];
        try
        {
            for (var frame = 0; frame < MaximumFramesPerQuery; frame++)
            {
                var count = await transaction.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                // Other raw HID traffic is skipped until the battery report arrives.
                if (KeychronBatteryReportDecoding.IsQ1HeBatteryReport(buffer.AsSpan(0, count)))
                {
                    return buffer.AsSpan(0, count).ToArray();
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw NoReport();
        }

        throw new BatteryAdapterException(BatteryAdapterErrorKind.ProtocolError,
            $"The Keychron Link receiver sent {MaximumFramesPerQuery} frames without a Q1 HE battery report.");
    }

    private static BatteryAdapterException NoReport() => new(BatteryAdapterErrorKind.DeviceOffline,
        "Keychron Q1 HE sent no battery report through the Keychron Link receiver " +
        "(keyboard asleep/off, or not running the battery-monitor firmware).");
}

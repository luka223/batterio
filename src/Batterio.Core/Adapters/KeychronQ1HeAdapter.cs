using System.Diagnostics;
using System.Text.Json;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

/// <summary>
/// Reads the Q1 HE battery from reports the custom firmware pushes through the Keychron Link dongle.
/// The keyboard ignores requests over 2.4 GHz, so this adapter only listens and never writes.
/// </summary>
public sealed class KeychronQ1HeAdapter : IBatteryAdapter
{
    public const int MaximumFramesPerQuery = 64;
    public static readonly TimeSpan DefaultListenTimeout = TimeSpan.FromSeconds(4);

    private readonly IHidCollectionProvider _provider;
    private readonly IBatterioLog _log;
    private readonly string? _devicePath;
    private readonly TimeSpan _listenTimeout;
    private int _disposed;
    private readonly CancellationTokenSource _lifetime = new();

    public KeychronQ1HeAdapter()
        : this(new EmptyHidCollectionProvider(), null, null)
    {
    }

    public KeychronQ1HeAdapter(
        IHidCollectionProvider provider,
        IBatterioLog? log = null,
        string? devicePath = null,
        TimeSpan? listenTimeout = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _log = log ?? new NullBatterioLog();
        _devicePath = devicePath;
        _listenTimeout = listenTimeout ?? DefaultListenTimeout;
    }

    public async Task<BatteryReading> GetBatteryStatus(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var started = Stopwatch.GetTimestamp();
        try { return await QueryAsync(attempt.Token).ConfigureAwait(false); }
        catch (BatteryAdapterException error)
        {
            _log.Log(BatterioLogLevel.Warning, $"Keychron query failed ({error.Kind}): {error.Message}");
            throw;
        }
        finally
        {
            _log.Log(BatterioLogLevel.Information, $"Keychron query finished in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
        }
    }

    private async Task<BatteryReading> QueryAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        var query = KeychronDefinitions.LinkRawHidQuery;
        var collections = _provider.Enumerate(query)
            .Where(query.Matches)
            .Where(collection => _devicePath is null ||
                                 string.Equals(collection.DevicePath, _devicePath, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (collections.Length == 0)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.ReceiverMissing,
                _devicePath is null
                    ? "The Keychron Link receiver was not found."
                    : $"The configured Keychron Link receiver path '{_devicePath}' was not found.");
        }

        if (_devicePath is null && collections.Length > 1)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.ReceiverAmbiguous,
                "More than one Keychron Link raw HID collection matched; configure devicePath to select one.");
        }

        var collection = collections[0];
        await using var transport = await _provider.OpenAsync(collection, cancellationToken).ConfigureAwait(false);
        await using var rawTransaction = await transport.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var transaction = new TracingHidTransaction(rawTransaction, _log, collection.DevicePath);

        var frame = await ListenForReportAsync(transaction, collection, cancellationToken).ConfigureAwait(false);
        var reading = KeychronBatteryReportDecoding.Decode(frame, _log);
        _log.Log(BatterioLogLevel.Information,
            $"Keychron Q1 HE {reading.Percentage}% ({reading.ChargeState}), " +
            $"{KeychronBatteryReportDecoding.DecodeMillivolts(frame)} mV, charge byte 0x{frame[KeychronBatteryReportDecoding.ChargeOffset]:X2}, " +
            $"connection 0x{frame[KeychronBatteryReportDecoding.ConnectionOffset]:X2}.");
        return reading;
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
            throw new BatteryAdapterException(BatteryAdapterErrorKind.DeviceOffline,
                "Keychron Q1 HE sent no battery report through the Keychron Link receiver " +
                "(keyboard asleep/off, or not running the battery-monitor firmware).");
        }

        throw new BatteryAdapterException(BatteryAdapterErrorKind.ProtocolError,
            $"The Keychron Link receiver sent {MaximumFramesPerQuery} frames without a Q1 HE battery report.");
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _lifetime.Cancel();
        return ValueTask.CompletedTask;
    }
}

public sealed class KeychronQ1HeAdapterFactory : IBatteryAdapterFactory
{
    private readonly IHidCollectionProvider _provider;
    private readonly IBatterioLog _log;

    public KeychronQ1HeAdapterFactory()
        : this(new EmptyHidCollectionProvider(), null)
    {
    }

    public KeychronQ1HeAdapterFactory(IHidCollectionProvider provider, IBatterioLog? log = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _log = log ?? new NullBatterioLog();
    }

    public string AdapterId => "keychron-q1-he";

    public void ValidateSettings(JsonElement settings) => HidAdapterSettings.ReadDevicePath(settings, AdapterId);

    public IBatteryAdapter CreateAdapter(DeviceConfiguration device) =>
        new KeychronQ1HeAdapter(
            _provider,
            _log,
            HidAdapterSettings.ReadDevicePath(device.AdapterSettings, AdapterId));
}

using System.Diagnostics;
using System.Text.Json;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

public sealed class LogitechGProX2LightspeedAdapter : IBatteryAdapter
{
    public const ushort VendorId = 0x046D;
    public const ushort ProductId = 0x0AF7;
    public const ushort InterfaceNumber = 3;
    public const ushort UsagePage = 0xFFA0;
    public const ushort Usage = 0x0001;

    private readonly IHidCollectionProvider _provider;
    private readonly IBatterioLog _log;
    private readonly string? _devicePath;
    private int _disposed;
    private readonly CancellationTokenSource _lifetime = new();

    public LogitechGProX2LightspeedAdapter()
        : this(new EmptyHidCollectionProvider(), null, null)
    {
    }

    public LogitechGProX2LightspeedAdapter(
        IHidCollectionProvider provider,
        IBatterioLog? log = null,
        string? devicePath = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _log = log ?? new NullBatterioLog();
        _devicePath = devicePath;
    }

    public async Task<BatteryReading> GetBatteryStatus(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var started = Stopwatch.GetTimestamp();
        try { return await QueryAsync(attempt.Token).ConfigureAwait(false); }
        catch (BatteryAdapterException error)
        {
            _log.Log(BatterioLogLevel.Warning, $"Logitech query failed ({error.Kind}): {error.Message}");
            throw;
        }
        finally
        {
            _log.Log(BatterioLogLevel.Information, $"Logitech query finished in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
        }
    }

    private async Task<BatteryReading> QueryAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();

        var query = LogitechReceiverDefinitions.LightspeedQuery;
        var collections = _provider.Enumerate(query)
            .Where(query.Matches)
            .Where(collection => _devicePath is null ||
                                 string.Equals(collection.DevicePath, _devicePath, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (collections.Length == 0)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.ReceiverMissing,
                _devicePath is null
                    ? "The Logitech LIGHTSPEED headset receiver was not found."
                    : $"The configured Logitech LIGHTSPEED receiver path '{_devicePath}' was not found.");
        }

        if (_devicePath is null && collections.Length > 1)
        {
            throw new BatteryAdapterException(BatteryAdapterErrorKind.ReceiverAmbiguous,
                "More than one Logitech LIGHTSPEED headset battery collection matched; configure devicePath to select one.");
        }

        var collection = collections[0];
        await using var protocolGate = await LogitechProtocolGate.EnterAsync(collection.DevicePath, cancellationToken).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await using var transport = await _provider.OpenAsync(collection, cancellationToken).ConfigureAwait(false);
        await using var transaction = await transport.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var modern = new CenturionClient(new TracingHidTransaction(transaction, _log, collection.DevicePath), collection, _log);
        try
        {
            var reading = await modern.GetBatteryAsync(cancellationToken).ConfigureAwait(false);
            _log.Log(BatterioLogLevel.Information,
                $"LIGHTSPEED headset battery query on '{collection.DevicePath}' completed in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
            return reading;
        }
        catch (BatteryAdapterException exception) when (exception.Kind == BatteryAdapterErrorKind.UnsupportedCapability)
        {
            _log.Log(BatterioLogLevel.Information,
                $"Modern Centurion battery support was positively rejected; trying the narrow legacy battery request on '{collection.DevicePath}'.");
            return await modern.GetLegacyBatteryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _lifetime.Cancel();
        return ValueTask.CompletedTask;
    }
}

public sealed class LogitechGProX2LightspeedAdapterFactory : IBatteryAdapterFactory
{
    private readonly IHidCollectionProvider _provider;
    private readonly IBatterioLog _log;

    public LogitechGProX2LightspeedAdapterFactory()
        : this(new EmptyHidCollectionProvider(), null)
    {
    }

    public LogitechGProX2LightspeedAdapterFactory(IHidCollectionProvider provider, IBatterioLog? log = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _log = log ?? new NullBatterioLog();
    }

    public string AdapterId => "logitech-g-pro-x2-lightspeed";

    public void ValidateSettings(JsonElement settings) => HidAdapterSettings.ReadDevicePath(settings, AdapterId);

    public IBatteryAdapter CreateAdapter(DeviceConfiguration device) =>
        new LogitechGProX2LightspeedAdapter(
            _provider,
            _log,
            HidAdapterSettings.ReadDevicePath(device.AdapterSettings, AdapterId));
}

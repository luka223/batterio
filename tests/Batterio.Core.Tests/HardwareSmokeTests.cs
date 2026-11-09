using Batterio.App.Windows;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

public sealed class HardwareSmokeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [HardwareFact]
    public async Task SuperstrikeShortCollectionAnswersProtocolPing()
    {
        var provider = new WindowsHidCollectionProvider();
        var collections = provider.Enumerate(new HidCollectionQuery
        {
            VendorId = 0x046D,
            ProductId = 0xC54D,
            InterfaceNumber = 2,
            UsagePage = 0xFF00,
            RequiredReportId = 0x10
        });
        var collection = Assert.Single(collections);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var transport = await provider.OpenAsync(collection, timeout.Token);
        await using var transaction = await transport.BeginTransactionAsync(timeout.Token);
        var client = new HidppClient(transaction, collection, new TestLog(output));
        Assert.NotNull(await client.PingAsync(1, timeout.Token));
    }

    [HardwareFact]
    public async Task SuperstrikeWiredMouseCanBeEnumeratedOpenedAndQueried()
    {
        var provider = new WindowsHidCollectionProvider();
        var query = LogitechGProX2Superstrike.WiredQuery;
        var collection = Assert.Single(provider.Enumerate(query));
        Assert.True(query.Matches(collection));

        await using var adapter = RouteHarness.Superstrike(
            provider, collection.DevicePath, new TestLog(output));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reading = await adapter.GetBatteryStatus(timeout.Token);

        Assert.InRange(reading.Percentage ?? throw new Xunit.Sdk.XunitException("Wired battery response did not include a percentage."), 0, 100);
        Assert.True(reading.ChargeState is BatteryChargeState.Charging or BatteryChargeState.NotCharging);
    }

    [HardwareFact]
    public async Task SuperstrikeReceiverCanBeEnumeratedOpenedAndQueried()
    {
        var provider = new WindowsHidCollectionProvider();
        var collection = Assert.Single(provider.Enumerate(LogitechGProX2Superstrike.ReceiverQuery));
        await using var adapter = RouteHarness.Superstrike(provider, collection.DevicePath, new TestLog(output));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reading = await adapter.GetBatteryStatus(timeout.Token);
        Assert.True(reading.ChargeState is BatteryChargeState.Charging or BatteryChargeState.NotCharging);
    }

    [HardwareFact]
    public async Task LightspeedReceiverCanBeEnumeratedOpenedAndQueried()
    {
        await using var adapter = RouteHarness.Lightspeed(new WindowsHidCollectionProvider(), log: new TestLog(output));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.NotNull(await adapter.GetBatteryStatus(timeout.Token));
    }

    // Recorded on a real hub (USB-1 mode, PID 0x2290): the status collection is interface 3, usage page 0xFFC0,
    // usage 0x0001, 64-byte input and output reports, report ID 1. Interface 3 also exposes a 0xFF00 collection
    // (report ID 7) that is not used. WriteFile on the collection reaches the hub, so no HidD_SetOutputReport fallback is needed.
    [HardwareFact]
    public void OmniHidCollectionsAreListed()
    {
        var provider = new WindowsHidCollectionProvider();
        var collections = provider.Enumerate(new HidCollectionQuery { VendorId = 0x1038, ProductId = 0x2290 });
        Assert.NotEmpty(collections);
        foreach (var collection in collections)
        {
            output.WriteLine(
                $"path={collection.DevicePath} interface={collection.InterfaceNumber} " +
                $"usagePage=0x{collection.UsagePage:X4} usage=0x{collection.Usage:X4} " +
                $"in={collection.InputReportLength} out={collection.OutputReportLength} feature={collection.FeatureReportLength} " +
                $"reportIds=[{string.Join(",", collection.SupportedReportIds ?? new HashSet<byte>())}]");
        }
    }

    // Observed reply (headset on and discharging, reserve slot charging):
    // 01B0 0000 0100 61 51 0A01 0204 0500 08 08 03 ... -> [6]=0x61 (97%), [7]=0x51 (81%), [0x0E]=0x08, [0x0F]=0x08.
    // Observed reply with the reserve slot empty:
    // 01B0 0000 0100 5F 00 0A01 0204 0500 08 08 03 ... -> [6]=0x5F (95%), [7]=0x00.
    // Only [6] (the headset level) and [7] differ from the capture above, so there is no separate presence byte:
    // an empty slot reports 0 in the reserve byte.
    [HardwareFact]
    public async Task OmniRawStatusProbe()
    {
        var provider = new WindowsHidCollectionProvider();
        var collection = Assert.Single(provider.Enumerate(new HidCollectionQuery
        {
            VendorId = 0x1038,
            ProductId = 0x2290,
            InterfaceNumber = 3,
            UsagePage = 0xFFC0
        }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var transport = await provider.OpenAsync(collection, timeout.Token);
        await using var transaction = await transport.BeginTransactionAsync(timeout.Token);

        var request = new byte[collection.OutputReportLength];
        request[0] = 0x01;
        request[1] = 0xB0;
        await transaction.WriteAsync(request, timeout.Token);

        var buffer = new byte[collection.InputReportLength];
        for (var frame = 0; frame < 32; frame++)
        {
            var count = await transaction.ReadAsync(buffer, timeout.Token);
            output.WriteLine($"RX {Convert.ToHexString(buffer.AsSpan(0, count))}");
            if (count >= 2 && buffer[0] == 0x01 && buffer[1] == 0xB0)
            {
                output.WriteLine(
                    $"headset={buffer[0x06]} reserve={buffer[0x07]} link=0x{buffer[0x0E]:X2} charging=0x{buffer[0x0F]:X2}");
                return;
            }
        }

        throw new Xunit.Sdk.XunitException("No 01 B0 status reply received.");
    }

    [HardwareFact]
    public async Task NovaProOmniHubCanBeEnumeratedOpenedAndQueried()
    {
        var provider = new WindowsHidCollectionProvider();
        var collection = Assert.Single(provider.Enumerate(SteelSeriesArctisNovaProOmni.NovaProOmniQuery));
        await using var adapter = RouteHarness.Omni(provider, collection.DevicePath, new TestLog(output));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var reading = await adapter.GetBatteryStatus(timeout.Token);

        output.WriteLine($"headset={reading.Percentage} ({reading.ChargeState}) reserve={reading.Reserve?.Percentage} ({reading.Reserve?.ChargeState})");
        if (reading.ChargeState != BatteryChargeState.Offline)
        {
            Assert.InRange(reading.Percentage ?? throw new Xunit.Sdk.XunitException("Online headset did not include a percentage."), 0, 100);
        }

        if (reading.Reserve is { } reserve)
        {
            Assert.InRange(reserve.Percentage ?? throw new Xunit.Sdk.XunitException("Reserve did not include a percentage."), 1, 100);
        }
    }

    [HardwareFact]
    public async Task KeychronQ1HeReportsThroughLinkReceiver()
    {
        await using var adapter = RouteHarness.Keychron(new WindowsHidCollectionProvider(), log: new TestLog(output));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reading = await adapter.GetBatteryStatus(timeout.Token);
        output.WriteLine($"Keychron Q1 HE: {reading.Percentage}% ({reading.ChargeState})");
        Assert.NotEqual(BatteryChargeState.Offline, reading.ChargeState);
    }

    [HardwareFact]
    public async Task MxMaster3sCanBeQueried()
    {
        var reading = await ReadMxAsync(LogitechMxMaster3s.AdapterId);
        output.WriteLine($"MX Master 3S: {reading.Percentage}% ({reading.ChargeState})");
        Assert.NotEqual(BatteryChargeState.Offline, reading.ChargeState);
    }

    [HardwareFact]
    public async Task MxKeysMechanicalCanBeQueried()
    {
        var reading = await ReadMxAsync(LogitechMxKeysMechanical.AdapterId);
        output.WriteLine($"MX Keys Mechanical: {reading.Percentage}% ({reading.ChargeState})");
        Assert.NotEqual(BatteryChargeState.Offline, reading.ChargeState);
    }

    // Runs discovery for the MX devices exactly as the app does, then reads the device through its resolved routes.
    private async Task<BatteryReading> ReadMxAsync(string deviceId)
    {
        var log = new TestLog(output);
        var provider = new WindowsHidCollectionProvider();
        var routes = new DeviceRouteTable();
        await using var receivers = new LogiBoltReceiverManager(provider, log);
        var factories = new IBatteryAdapterFactory[]
        {
            new RoutedAdapterFactory(LogitechMxMaster3s.Profile, provider, receivers, routes, log),
            new RoutedAdapterFactory(LogitechMxKeysMechanical.Profile, provider, receivers, routes, log)
        };
        var catalog = BuiltInDevices.Catalog.Where(device => factories.Any(factory => factory.AdapterId == device.AdapterId)).ToArray();
        await using var discovery = new DeviceDiscoveryService(
            catalog, new AdapterRegistry(factories), provider, log, routes, receivers);
        var snapshot = await discovery.DiscoverAsync();
        foreach (var route in routes.GetRoutes(deviceId))
        {
            output.WriteLine($"route {route.Kind} '{route.EndpointPath}' slot {route.Slot} {route.State}");
        }

        Assert.True(snapshot.Devices.Single(device => device.DeviceId == deviceId).IsPresent, $"{deviceId} was not found.");
        var adapter = factories.Single(factory => factory.AdapterId == deviceId)
            .CreateAdapter(BuiltInDevices.CatalogById[deviceId].CreateConfiguration());
        await using (adapter)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            return await adapter.GetBatteryStatus(timeout.Token);
        }
    }

    // Reverse-engineering aid: dumps what every paired Logi Bolt slot reports so the model IDs and names in
    // the MX device profiles can be confirmed or corrected. Send the output (and batterio.log) to the maintainers.
    [HardwareFact]
    public async Task BoltReceiverSlotsAreListed()
    {
        var provider = new WindowsHidCollectionProvider();
        var receivers = provider.Enumerate(LogiBoltReceiverManager.BoltReceiverQuery)
            .Where(LogiBoltReceiverManager.BoltReceiverQuery.Matches)
            .ToArray();
        Assert.NotEmpty(receivers);
        var log = new TestLog(output);
        foreach (var receiver in receivers)
        {
            output.WriteLine($"receiver path={receiver.DevicePath} in={receiver.InputReportLength} out={receiver.OutputReportLength}");
            await using var transport = await provider.OpenAsync(receiver, CancellationToken.None);
            await using var transaction = await transport.BeginTransactionAsync(CancellationToken.None);
            var client = new HidppClient(transaction, receiver, log);
            for (byte slot = 1; slot <= 6; slot++)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    var ping = await client.PingAsync(slot, timeout.Token);
                    output.WriteLine($"slot {slot}: ping ok, HID++ {ping.Parameters[0]}.{ping.Parameters[1]}");

                    var name = await LogitechDeviceName.ReadAsync(client, slot, timeout.Token);
                    output.WriteLine($"slot {slot}: name '{name ?? "unavailable"}'");

                    var infoIndex = await client.ResolveFeatureAsync(slot, 0x0003, timeout.Token);
                    var info = await client.SendAsync(slot, infoIndex, 0, ReadOnlyMemory<byte>.Empty, timeout.Token);
                    output.WriteLine($"slot {slot}: {LogitechDeviceInformation.FormatModelIds(info.Parameters)}; raw {Convert.ToHexString(info.Parameters)}");

                    foreach (var feature in new ushort[] { 0x1000, 0x1004 })
                    {
                        try
                        {
                            var index = await client.ResolveFeatureAsync(slot, feature, timeout.Token);
                            output.WriteLine($"slot {slot}: feature 0x{feature:X4} present at index 0x{index:X2}");
                        }
                        catch (BatteryAdapterException exception) when (exception.Kind == BatteryAdapterErrorKind.UnsupportedCapability)
                        {
                            output.WriteLine($"slot {slot}: feature 0x{feature:X4} absent");
                        }
                    }
                }
                catch (Exception exception) when (exception is BatteryAdapterException or OperationCanceledException)
                {
                    output.WriteLine($"slot {slot}: {exception.GetType().Name}: {exception.Message}");
                }
            }
        }
    }

    // ----- Research aids for wake-up detection (routes plan, steps 12 and 14) -----
    // Each test keeps the collections of one receiver or hub open for BATTERIO_LISTEN_SECONDS (default 60) and logs every frame that arrives
    // without a request, with the time it arrived. Run one while the device sleeps, wakes, goes onto its cable and back, or is switched off
    // and on, and note which frame (if any) reliably marks "the device is there". Nothing is written to the device.

    [HardwareFact]
    public Task ListenForUnsolicitedFramesOnTheSuperstrikeReceiver() => ListenAsync(
        LogitechGProX2Superstrike.ReceiverQuery,
        LogitechGProX2Superstrike.ReceiverQuery with { MinimumReportLength = 7, RequiredReportId = 0x10 });

    [HardwareFact]
    public Task ListenForUnsolicitedFramesOnTheLightspeedHeadsetReceiver() => ListenAsync(LogitechGProX2Lightspeed.LightspeedQuery);

    [HardwareFact]
    public Task ListenForUnsolicitedFramesOnTheOmniHub() => ListenAsync(SteelSeriesArctisNovaProOmni.NovaProOmniQuery);

    private async Task ListenAsync(params HidCollectionQuery[] queries)
    {
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("BATTERIO_LISTEN_SECONDS"), out var configured) ? configured : 60;
        var provider = new WindowsHidCollectionProvider();
        var collections = queries
            .SelectMany(query => provider.Enumerate(query).Where(query.Matches))
            .DistinctBy(collection => collection.DevicePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.NotEmpty(collections);
        output.WriteLine($"Listening for {seconds} s on {collections.Length} collection(s); trigger the events you want to observe now.");

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var frames = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var listeners = collections.Select(async collection =>
        {
            await using var transport = await provider.OpenAsync(collection, deadline.Token);
            await using var transaction = await transport.BeginTransactionAsync(deadline.Token);
            var buffer = new byte[Math.Max(collection.InputReportLength, 64)];
            try
            {
                while (true)
                {
                    var count = await transaction.ReadAsync(buffer, deadline.Token);
                    var at = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
                    Interlocked.Increment(ref frames);
                    lock (output)
                    {
                        output.WriteLine($"+{at,7:F1}s  report 0x{buffer[0]:X2} on {collection.DevicePath}: {Convert.ToHexString(buffer.AsSpan(0, count))}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }).ToArray();
        await Task.WhenAll(listeners);
        output.WriteLine($"{Volatile.Read(ref frames)} frame(s) arrived without a request.");
    }

    private sealed class TestLog(Xunit.Abstractions.ITestOutputHelper output) : IBatterioLog
    {
        public void Log(BatterioLogLevel level, string message, Exception? exception = null) => output.WriteLine(message);
    }

    private sealed class HardwareFactAttribute : FactAttribute
    {
        public HardwareFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("BATTERIO_HARDWARE_TESTS") != "1")
                Skip = "Requires Windows and attached powered-on Logitech HID hardware; opt in with BATTERIO_HARDWARE_TESTS=1.";
        }
    }
}

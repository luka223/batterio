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
        var query = LogitechReceiverDefinitions.SuperstrikeWiredQuery;
        var collection = Assert.Single(provider.Enumerate(query));
        Assert.True(query.Matches(collection));

        await using var adapter = new LogitechGProX2SuperstrikeAdapter(
            provider, new TestLog(output), collection.DevicePath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reading = await adapter.GetBatteryStatus(timeout.Token);

        Assert.InRange(reading.Percentage ?? throw new Xunit.Sdk.XunitException("Wired battery response did not include a percentage."), 0, 100);
        Assert.True(reading.ChargeState is BatteryChargeState.Charging or BatteryChargeState.NotCharging);
    }

    [HardwareFact]
    public async Task SuperstrikeReceiverCanBeEnumeratedOpenedAndQueried()
    {
        var provider = new WindowsHidCollectionProvider();
        var collection = Assert.Single(provider.Enumerate(LogitechReceiverDefinitions.SuperstrikeReceiverQuery));
        await using var adapter = new LogitechGProX2SuperstrikeAdapter(provider, new TestLog(output), collection.DevicePath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reading = await adapter.GetBatteryStatus(timeout.Token);
        Assert.True(reading.ChargeState is BatteryChargeState.Charging or BatteryChargeState.NotCharging);
    }

    [HardwareFact]
    public async Task LightspeedReceiverCanBeEnumeratedOpenedAndQueried()
    {
        await using var adapter = new LogitechGProX2LightspeedAdapter(new WindowsHidCollectionProvider(), new TestLog(output));
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
        var collection = Assert.Single(provider.Enumerate(SteelSeriesDefinitions.NovaProOmniQuery));
        await using var adapter = new SteelSeriesArctisNovaProOmniAdapter(provider, new TestLog(output), collection.DevicePath);
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
    public async Task MxMaster3sCanBeQueried()
    {
        await using var adapter = new LogitechBoltAdapter(
            LogitechBoltDeviceProfile.MxMaster3s, new WindowsHidCollectionProvider(), new TestLog(output));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var reading = await adapter.GetBatteryStatus(timeout.Token);
        output.WriteLine($"MX Master 3S: {reading.Percentage}% ({reading.ChargeState})");
        Assert.NotEqual(BatteryChargeState.Offline, reading.ChargeState);
    }

    [HardwareFact]
    public async Task MxKeysMechanicalCanBeQueried()
    {
        await using var adapter = new LogitechBoltAdapter(
            LogitechBoltDeviceProfile.MxKeysMechanical, new WindowsHidCollectionProvider(), new TestLog(output));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var reading = await adapter.GetBatteryStatus(timeout.Token);
        output.WriteLine($"MX Keys Mechanical: {reading.Percentage}% ({reading.ChargeState})");
        Assert.NotEqual(BatteryChargeState.Offline, reading.ChargeState);
    }

    // Reverse-engineering aid: dumps what every paired Logi Bolt slot reports so the model IDs and names in
    // LogitechBoltDeviceProfile can be confirmed or corrected. Send the output (and batterio.log) to the maintainers.
    [HardwareFact]
    public async Task BoltReceiverSlotsAreListed()
    {
        var provider = new WindowsHidCollectionProvider();
        var receivers = provider.Enumerate(LogitechReceiverDefinitions.BoltReceiverQuery)
            .Where(LogitechReceiverDefinitions.BoltReceiverQuery.Matches)
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

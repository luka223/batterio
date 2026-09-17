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

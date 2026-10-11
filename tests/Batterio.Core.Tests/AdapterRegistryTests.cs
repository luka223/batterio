using System.Text.Json;
using Batterio.Core;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

public sealed class AdapterRegistryTests
{
    [Fact]
    public void DuplicateAdapterIdsAreRejectedAtConstruction()
    {
        var exception = Assert.Throws<ArgumentException>(() => new AdapterRegistry(new[]
        {
            new TestFactory("same"),
            new TestFactory("same")
        }));

        Assert.Contains("more than once", exception.Message);
    }

    [Fact]
    public void EachDeviceGetsAnIndependentAdapterInstanceAndSettings()
    {
        var factory = new TestFactory("test");
        var registry = new AdapterRegistry(new[] { factory });
        var first = Device("one", "alpha");
        var second = Device("two", "beta");

        var adapter1 = registry.CreateAdapter(first);
        var adapter2 = registry.CreateAdapter(second);

        Assert.NotSame(adapter1, adapter2);
        Assert.Equal(new[] { "alpha", "beta" }, factory.Settings);
    }

    [Fact]
    public void UnknownAdapterIsPerDeviceConfigurationError()
    {
        var registry = new AdapterRegistry(Array.Empty<IBatteryAdapterFactory>());
        var exception = Assert.Throws<AdapterConfigurationException>(() => registry.CreateAdapter(Device("one", "unknown")));
        Assert.Contains("not registered", exception.Message);
    }

    [Fact]
    public void ObsoleteKeychronConfigurationRemainsAnUnknownAdapter()
    {
        var registry = new AdapterRegistry(Array.Empty<IBatteryAdapterFactory>());
        var exception = Assert.Throws<AdapterConfigurationException>(() => registry.CreateAdapter(new DeviceConfiguration
        {
            Id = "obsolete",
            Name = "Obsolete",
            AdapterId = "keychron-q1-he",
            AdapterSettings = JsonDocument.Parse("{}").RootElement
        }));

        Assert.Contains("keychron-q1-he", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not registered", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LogitechFactoriesValidateOptionalDevicePath()
    {
        var valid = JsonDocument.Parse("{\"devicePath\":\"receiver\"}").RootElement;
        new LogitechGProX2SuperstrikeAdapterFactory().ValidateSettings(valid);
        new LogitechGProX2LightspeedAdapterFactory().ValidateSettings(valid);
        new LogitechMxMaster3sAdapterFactory().ValidateSettings(valid);
        new LogitechMxKeysMechanicalAdapterFactory().ValidateSettings(valid);

        var exception = Assert.Throws<AdapterConfigurationException>(() =>
            new LogitechGProX2LightspeedAdapterFactory().ValidateSettings(
                JsonDocument.Parse("{\"devicePath\":\"\"}").RootElement));
        Assert.Contains("devicePath", exception.Message, StringComparison.OrdinalIgnoreCase);

        var invalid = JsonDocument.Parse("{\"devicePath\":\"\"}").RootElement;
        Assert.Throws<AdapterConfigurationException>(() => new LogitechMxMaster3sAdapterFactory().ValidateSettings(invalid));
        Assert.Throws<AdapterConfigurationException>(() => new LogitechMxKeysMechanicalAdapterFactory().ValidateSettings(invalid));
    }

    private static DeviceConfiguration Device(string id, string setting) => new()
    {
        Id = id,
        Name = id,
        AdapterId = "test",
        AdapterSettings = JsonDocument.Parse($"{{\"value\":\"{setting}\"}}").RootElement
    };

    private sealed class TestFactory(string id) : IBatteryAdapterFactory
    {
        public string AdapterId { get; } = id;
        public List<string> Settings { get; } = new();

        public void ValidateSettings(JsonElement settings)
        {
            Settings.Add(settings.GetProperty("value").GetString()!);
        }

        public IBatteryAdapter CreateAdapter(DeviceConfiguration device) => new TestAdapter();
    }

    private sealed class TestAdapter : IBatteryAdapter
    {
        public Task<BatteryReading> GetBatteryStatus(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BatteryReading(50, BatteryChargeState.NotCharging));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

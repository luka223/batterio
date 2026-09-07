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
        public Task<int> GetBatteryStatus(CancellationToken cancellationToken = default) => Task.FromResult(50);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

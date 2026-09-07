using System.Text.Json;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

public sealed class AdapterConfigurationException : Exception
{
    public AdapterConfigurationException(string message)
        : base(message)
    {
    }

    public AdapterConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class AdapterRegistry
{
    private readonly IReadOnlyDictionary<string, IBatteryAdapterFactory> _factories;

    public AdapterRegistry(IEnumerable<IBatteryAdapterFactory> factories)
    {
        var map = new Dictionary<string, IBatteryAdapterFactory>(StringComparer.OrdinalIgnoreCase);
        foreach (var factory in factories)
        {
            if (factory is null || string.IsNullOrWhiteSpace(factory.AdapterId))
            {
                throw new ArgumentException("Every adapter factory must provide a nonempty AdapterId.", nameof(factories));
            }

            if (!map.TryAdd(factory.AdapterId, factory))
            {
                throw new ArgumentException($"Adapter id '{factory.AdapterId}' is registered more than once.", nameof(factories));
            }
        }

        _factories = map;
    }

    public IReadOnlyCollection<string> AdapterIds => _factories.Keys.ToArray();

    public bool TryGetFactory(string adapterId, out IBatteryAdapterFactory? factory) =>
        _factories.TryGetValue(adapterId, out factory);

    public IBatteryAdapter CreateAdapter(DeviceConfiguration device)
    {
        if (!TryGetFactory(device.AdapterId, out var factory) || factory is null)
        {
            throw new AdapterConfigurationException($"Adapter '{device.AdapterId}' is not registered.");
        }

        try
        {
            if (device.AdapterSettings.ValueKind == JsonValueKind.Undefined)
            {
                using var emptySettings = JsonDocument.Parse("{}");
                factory.ValidateSettings(emptySettings.RootElement);
            }
            else
            {
                factory.ValidateSettings(device.AdapterSettings);
            }

            return factory.CreateAdapter(device);
        }
        catch (AdapterConfigurationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AdapterConfigurationException(
                $"Adapter '{device.AdapterId}' could not create device '{device.Id}'.", exception);
        }
    }
}

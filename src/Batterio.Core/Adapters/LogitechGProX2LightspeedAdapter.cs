using System.Text.Json;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

public sealed class LogitechGProX2LightspeedAdapter : IBatteryAdapter
{
    public Task<int> GetBatteryStatus(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(15);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class LogitechGProX2LightspeedAdapterFactory : IBatteryAdapterFactory
{
    public string AdapterId => "logitech-g-pro-x2-lightspeed";
    public void ValidateSettings(JsonElement settings) { }
    public IBatteryAdapter CreateAdapter(DeviceConfiguration device) => new LogitechGProX2LightspeedAdapter();
}

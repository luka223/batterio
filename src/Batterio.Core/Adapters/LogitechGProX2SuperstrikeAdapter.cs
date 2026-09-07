using System.Text.Json;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

public sealed class LogitechGProX2SuperstrikeAdapter : IBatteryAdapter
{
    public Task<int> GetBatteryStatus(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(64);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class LogitechGProX2SuperstrikeAdapterFactory : IBatteryAdapterFactory
{
    public string AdapterId => "logitech-g-pro-x2-superstrike";
    public void ValidateSettings(JsonElement settings) { }
    public IBatteryAdapter CreateAdapter(DeviceConfiguration device) => new LogitechGProX2SuperstrikeAdapter();
}

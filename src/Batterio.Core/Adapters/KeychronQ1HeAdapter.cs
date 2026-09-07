using System.Text.Json;
using Batterio.Core.Abstractions;

namespace Batterio.Core;

public sealed class KeychronQ1HeAdapter : IBatteryAdapter
{
    public Task<int> GetBatteryStatus(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(35);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class KeychronQ1HeAdapterFactory : IBatteryAdapterFactory
{
    public string AdapterId => "keychron-q1-he";
    public void ValidateSettings(JsonElement settings) { }
    public IBatteryAdapter CreateAdapter(DeviceConfiguration device) => new KeychronQ1HeAdapter();
}

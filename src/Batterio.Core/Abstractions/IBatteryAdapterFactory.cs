using System.Text.Json;

namespace Batterio.Core.Abstractions;

public interface IBatteryAdapterFactory
{
    string AdapterId { get; }

    void ValidateSettings(JsonElement settings);

    IBatteryAdapter CreateAdapter(DeviceConfiguration device);
}

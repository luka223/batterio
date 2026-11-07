namespace Batterio.Core;

/// <summary>
/// One device entry on the taskbar strip. Only the main battery is represented.
/// A <c>null</c> percentage means the device is offline or has no known level; it is drawn dimmed.
/// </summary>
public sealed record TaskbarStripItem(string DeviceId, DeviceKind Kind, int? Percentage, bool IsCharging)
{
    public bool IsOffline => Percentage is null;
}

/// <summary>Framework-free projection of device state into what the taskbar strip draws.</summary>
public static class TaskbarStripModel
{
    /// <summary>
    /// Returns the devices to draw, in device list order so the strip does not shuffle as devices
    /// come and go. A device is included when it is selected for the popup and opted in to the
    /// taskbar. Devices that are offline, disconnected or without a known percentage are included
    /// too, as offline items. The reserve battery is never consulted: the strip shows the main
    /// battery only.
    /// </summary>
    public static IReadOnlyList<TaskbarStripItem> Build(
        DeviceSelectionSnapshot selection,
        PollingSnapshot polling)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(polling);

        var readings = new Dictionary<string, BatteryReading>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in polling.States)
        {
            readings[state.Configuration.Id] = state.Reading;
        }

        var items = new List<TaskbarStripItem>();
        foreach (var device in selection.Devices)
        {
            if (!device.IsSelected || !device.IsOnTaskbar)
            {
                continue;
            }

            if (readings.TryGetValue(device.DeviceId, out var reading) &&
                reading.ChargeState != BatteryChargeState.Offline &&
                reading.Percentage is { } percentage)
            {
                items.Add(new TaskbarStripItem(
                    device.DeviceId,
                    device.Kind,
                    percentage,
                    reading.ChargeState == BatteryChargeState.Charging));
            }
            else
            {
                items.Add(new TaskbarStripItem(device.DeviceId, device.Kind, null, IsCharging: false));
            }
        }

        return items;
    }
}

using System.Runtime.InteropServices;
using System.Text.Json;
using Batterio.App.Windows;
using Batterio.Core;
using Batterio.Core.Abstractions;
using Batterio.Core.Notifications;
using Microsoft.Win32;

// Explicit manual acceptance tool, never registered in the production adapter catalog.
// Run Batterio once first so the probe can use its real persistent activation registration.
if (args.Length != 1 || args[0] is not (
        "activate" or "activate-fully-charged" or "notify" or "notify-fully-charged" or "history" or "history-fully-charged"))
{
    Console.Error.WriteLine("Usage: Batterio.NotificationProbe activate|activate-fully-charged|notify|notify-fully-charged|history|history-fully-charged");
    return 2;
}
string registeredExecutablePath;
using (var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{WindowsToastRegistrationService.ActivatorId:B}\LocalServer32"))
{
    if (key?.GetValue(null) is not string command)
        throw new InvalidOperationException("Run the published Batterio app once to register its activator.");
    Console.WriteLine("Registered server: " + command);
    if (command.Length < 2 || command[0] != '"')
        throw new InvalidOperationException("The registered activator command does not contain a quoted Batterio executable path.");
    var quoteEnd = command.IndexOf('"', 1);
    if (quoteEnd < 0)
        throw new InvalidOperationException("The registered activator command does not contain a quoted Batterio executable path.");
    registeredExecutablePath = command[1..quoteEnd];
    if (!File.Exists(registeredExecutablePath))
        throw new InvalidOperationException("The registered Batterio launcher no longer exists at its registered path.");
}

if (args[0] is "history" or "history-fully-charged")
{
    var expectedText = args[0] == "history-fully-charged"
        ? "Batterio test device battery is fully charged"
        : "Batterio test device battery level is below 30%";
    var testToasts = Windows.UI.Notifications.ToastNotificationManager.History
        .GetHistory(WindowsToastRegistrationService.AppUserModelId)
        .Where(toast => toast.Content.InnerText.Contains(expectedText, StringComparison.Ordinal))
        .ToArray();
    Console.WriteLine($"Persisted test notifications: {testToasts.Length}");
    foreach (var toast in testToasts) Console.WriteLine(toast.Content.GetXml());
    return testToasts.Length > 0 ? 0 : 1;
}

if (args[0] is "activate" or "activate-fully-charged")
{
    var launchArgument = args[0] == "activate-fully-charged" ? "fully-charged" : "low-battery";
    var clsid = WindowsToastRegistrationService.ActivatorId;
    var iid = typeof(IToastActivationCallback).GUID;
    Marshal.ThrowExceptionForHR(Native.CoCreateInstance(ref clsid, IntPtr.Zero, 4, ref iid, out var pointer));
    try
    {
        var callback = (IToastActivationCallback)Marshal.GetObjectForIUnknown(pointer);
        try { callback.Activate(WindowsToastRegistrationService.AppUserModelId, launchArgument, IntPtr.Zero, 0); }
        finally { if (Marshal.IsComObject(callback)) Marshal.FinalReleaseComObject(callback); }
    }
    finally { Marshal.Release(pointer); }
    Console.WriteLine("COM activation returned successfully.");
    return 0;
}

var fullyCharged = args[0] == "notify-fully-charged";
var log = new ProbeLog();
var platform = new ExistingRegistrationPlatform();
using var registration = new WindowsToastRegistrationService(platform, new(), () => registeredExecutablePath);
var service = new WindowsBatteryNotificationService(registration, platform, log);
await using var polling = new PollingCoordinator(new AdapterRegistry([new ScriptedFactory(fullyCharged)]));
await using var coordinator = new BatteryNotificationCoordinator(polling, service, log);
await coordinator.StartAsync();
await polling.SetActiveDevicesAsync([new DeviceConfiguration
{
    Id = "notification-acceptance", Name = "Batterio test device", AdapterId = "scripted-test-only",
    PollIntervalSeconds = 300, TimeoutSeconds = 1, AdapterSettings = JsonDocument.Parse("{}").RootElement
}]);
await platform.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
await polling.RefreshAllAsync();
await polling.RefreshAllAsync();
await polling.RefreshAllAsync();
await Task.Delay(1500); // Allow asynchronous Windows rejection to reach ToastNotification.Failed.
if (platform.Count != 1 || log.Failed) throw new InvalidOperationException("Notification acceptance probe failed.");
Console.WriteLine(fullyCharged
    ? "One native toast submitted after repeated 100% readings. Inspect its text, identity, persistence and click action."
    : "One native toast submitted after repeated low readings. Inspect its text, identity, persistence and click action.");
return 0;

sealed class ExistingRegistrationPlatform : IWindowsToastPlatform
{
    private readonly WindowsToastPlatform _native = new();
    public TaskCompletionSource Submitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Count;
    // Preserve the app's real registration: this probe must never become the toast activation target.
    public void SetProcessAppId(string appId) { }
    public void WriteShortcut(ToastShortcutRegistration registration) { }
    public void WriteComServer(Guid activatorId, string executablePath, string command) { }
    public IDisposable RegisterActivator(Guid activatorId, Action<string, string?> activated) => new NoopDisposable();
    public void Show(string appId, string xml, Action<Exception> failed)
    {
        _native.Show(appId, xml, failed);
        Interlocked.Increment(ref Count);
        Submitted.TrySetResult();
    }
    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
}
sealed class ScriptedFactory(bool fullyCharged) : IBatteryAdapterFactory
{
    public string AdapterId => "scripted-test-only";
    public void ValidateSettings(JsonElement settings) { }
    public IBatteryAdapter CreateAdapter(DeviceConfiguration device) => new ScriptedAdapter(fullyCharged);
    private sealed class ScriptedAdapter(bool fullyCharged) : IBatteryAdapter
    {
        public Task<BatteryReading> GetBatteryStatus(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BatteryReading(fullyCharged ? 100 : 29, BatteryChargeState.NotCharging));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
sealed class ProbeLog : IBatterioLog
{
    public volatile bool Failed;
    public void Log(BatterioLogLevel level, string message, Exception? exception = null)
    {
        if (level >= BatterioLogLevel.Warning) Failed = true;
        Console.WriteLine($"{level}: {message} {exception}");
    }
}
static class Native
{
    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);
}

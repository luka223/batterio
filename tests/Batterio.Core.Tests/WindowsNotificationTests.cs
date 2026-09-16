using System.Xml.Linq;
using Batterio.App.Windows;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

public sealed class WindowsNotificationTests
{
    [Theory]
    [InlineData("Mouse & <keyboard> \"quoted\" 'name'")]
    [InlineData("Miš 🖱️")]
    public async Task PayloadHasExactEscapedTextAndComActivation(string name)
    {
        var platform = new FakePlatform();
        using var registration = Registration(platform);
        await using var service = new WindowsBatteryNotificationService(registration, platform, new NullBatterioLog());
        await service.InitializeAsync();
        await service.ShowLowBatteryAsync("mouse", name);
        var toast = XElement.Parse(Assert.Single(platform.Toasts));
        Assert.Equal("foreground", (string?)toast.Attribute("activationType"));
        Assert.Equal("low-battery", (string?)toast.Attribute("launch"));
        Assert.Equal(name + " battery level is below 30%", Assert.Single(toast.Descendants("text")).Value);
        Assert.Equal("ToastGeneric", (string?)Assert.Single(toast.Descendants("binding")).Attribute("template"));
        Assert.Null(toast.Element("actions"));
        Assert.Null(toast.Element("audio"));
        Assert.Null(toast.Attribute("scenario"));
        Assert.Equal(WindowsToastRegistrationService.AppUserModelId, platform.ShownAppId);
    }

    [Fact]
    public void RegistrationIsIdempotentAndRepairsMovedExecutableOnNextRun()
    {
        var platform = new FakePlatform();
        using (var first = Registration(platform, @"C:\First folder\Batterio.App.exe"))
        {
            first.RegisterCurrentExecutable();
            first.RegisterCurrentExecutable();
            Assert.Equal(1, platform.RegisterCount);
            Assert.Equal(1, platform.ShortcutWrites);
            Assert.Equal(WindowsToastRegistrationService.ActivatorId, platform.Shortcut!.ActivatorId);
            Assert.Equal(WindowsToastRegistrationService.ActivationArgument, platform.Shortcut.Arguments);
            Assert.Equal(platform.ProcessAppId, platform.Shortcut.AppUserModelId);
            Assert.Equal("\"C:\\First folder\\Batterio.App.exe\" -ToastActivated", platform.Command);
            Assert.Equal(@"C:\First folder\Batterio.App.exe", platform.ServerExecutable);
        }
        using var second = Registration(platform, @"C:\Moved folder\Batterio.App.exe");
        second.RegisterCurrentExecutable();
        Assert.Equal(@"C:\Moved folder\Batterio.App.exe", platform.Shortcut!.ExecutablePath);
        Assert.Equal("\"C:\\Moved folder\\Batterio.App.exe\" -ToastActivated", platform.Command);
        Assert.Equal(@"C:\Moved folder\Batterio.App.exe", platform.ServerExecutable);
        Assert.Equal(2, platform.ShortcutWrites);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative.exe")]
    [InlineData("C:\\dotnet.exe")]
    [InlineData("C:\\DOTNET-HOST.EXE")]
    [InlineData("C:\\apphost.exe")]
    [InlineData("C:\\Batterio.dll")]
    public void InvalidExecutableNeverChangesRegistration(string? path)
    {
        var platform = new FakePlatform();
        using var registration = new WindowsToastRegistrationService(platform, new(), () => path, _ => true);
        Assert.Throws<InvalidOperationException>(registration.RegisterCurrentExecutable);
        Assert.Null(platform.ProcessAppId);
        Assert.Null(platform.Command);
    }

    [Fact]
    public void MissingExecutableNeverChangesRegistration()
    {
        var platform = new FakePlatform();
        using var registration = new WindowsToastRegistrationService(platform, new(), () => @"C:\Batterio.exe", _ => false);
        Assert.Throws<InvalidOperationException>(registration.RegisterCurrentExecutable);
        Assert.Null(platform.ProcessAppId);
    }

    [Fact]
    public async Task CleanupReleasesActivatorExactlyOnceAndPreservesPersistentRegistration()
    {
        var platform = new FakePlatform();
        using var registration = Registration(platform);
        var service = new WindowsBatteryNotificationService(registration, platform, new NullBatterioLog());
        await service.InitializeAsync();
        await service.DisposeAsync();
        await service.DisposeAsync();
        registration.Dispose();
        Assert.Equal(1, platform.DisposeCount);
        Assert.NotNull(platform.Shortcut);
        Assert.NotNull(platform.Command);
        Assert.Throws<ObjectDisposedException>(registration.RegisterCurrentExecutable);
    }

    [Fact]
    public void ColdAndRedirectedActivationWaitForReadinessAndStopAfterExit()
    {
        var activation = new WindowsToastActivation();
        var platform = new FakePlatform();
        using var registration = new WindowsToastRegistrationService(platform, activation, () => @"C:\Batterio.exe", _ => true);
        registration.RegisterCurrentExecutable();
        var calls = 0;
        platform.Activated!("different.app", "low-battery");
        activation.SetReady(() => calls++);
        Assert.Equal(0, calls);

        var cold = new WindowsToastActivation();
        cold.RequestPopup();
        cold.RequestPopup();
        cold.SetReady(() => calls++);
        Assert.Equal(1, calls);
        platform.Activated!(WindowsToastRegistrationService.AppUserModelId, "low-battery");
        Assert.Equal(2, calls);
        activation.Stop();
        activation.RequestPopup();
        activation.SetReady(() => calls++);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task AsynchronousWindowsFailureIsLogged()
    {
        var platform = new FakePlatform();
        var log = new RecordingLog();
        using var registration = Registration(platform);
        await using var service = new WindowsBatteryNotificationService(registration, platform, log);
        await service.InitializeAsync();
        await service.ShowLowBatteryAsync("mouse", "Mouse");
        var failure = new InvalidOperationException("Windows rejected the payload");
        platform.Failed!(failure);
        Assert.Same(failure, log.Exception);
    }

    private static WindowsToastRegistrationService Registration(FakePlatform platform, string path = @"C:\Batterio.exe") =>
        new(platform, new(), () => path, _ => true);

    private sealed class FakePlatform : IWindowsToastPlatform
    {
        public string? ProcessAppId, Command, ShownAppId, ServerExecutable;
        public ToastShortcutRegistration? Shortcut;
        public int RegisterCount, ShortcutWrites, DisposeCount;
        public Action<string, string?>? Activated;
        public Action<Exception>? Failed;
        public List<string> Toasts { get; } = [];
        public void SetProcessAppId(string appId) => ProcessAppId = appId;
        public void WriteShortcut(ToastShortcutRegistration registration) { Shortcut = registration; ShortcutWrites++; }
        public void WriteComServer(Guid id, string executablePath, string command)
        {
            Assert.Equal(WindowsToastRegistrationService.ActivatorId, id);
            Command = command;
            ServerExecutable = executablePath;
        }
        public IDisposable RegisterActivator(Guid id, Action<string, string?> activated)
        {
            Assert.Equal(WindowsToastRegistrationService.ActivatorId, id);
            RegisterCount++;
            Activated = activated;
            return new Cleanup(() => DisposeCount++);
        }
        public void Show(string appId, string xml, Action<Exception> failed) { ShownAppId = appId; Toasts.Add(xml); Failed = failed; }
    }

    private sealed class Cleanup(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    private sealed class RecordingLog : IBatterioLog
    {
        public Exception? Exception;
        public void Log(BatterioLogLevel level, string message, Exception? exception = null) => Exception = exception;
    }
}

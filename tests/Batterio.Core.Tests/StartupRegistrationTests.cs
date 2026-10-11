using Batterio.App.Windows;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace Batterio.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class StartupRegistrationTests
{
    [Fact]
    public async Task StartupRegistrationUsesTheBatterioValueAndQuotesTheExecutable()
    {
        using var fixture = new TempFile();
        var registry = new FakeRegistry();
        var service = new WindowsStartupRegistrationService(registry, () => fixture.Path);

        Assert.False(await service.IsRegisteredAsync());
        await service.SetRegisteredAsync(true);

        Assert.Equal($"\"{Path.GetFullPath(fixture.Path)}\"", registry.Key.Values[WindowsStartupRegistrationService.AppName]);
        Assert.True(await service.IsRegisteredAsync());
        await service.SetRegisteredAsync(false);
        Assert.False(await service.IsRegisteredAsync());
    }

    [Fact]
    public async Task InstalledRegistrationUsesPublicLauncherAndAcceptsExistingSameFolderEntry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Batterio-installed-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var launcher = Path.Combine(directory, "Batterio.App.exe");
        var runtime = Path.Combine(directory, "Batterio.Runtime.exe");
        File.WriteAllText(launcher, "launcher");
        File.WriteAllText(runtime, "runtime");
        try
        {
            var registry = new FakeRegistry();
            var paths = new InstalledExecutablePaths(launcher, runtime);
            var service = new WindowsStartupRegistrationService(
                registry,
                fileExists: File.Exists,
                installedPathsProvider: () => paths);

            registry.Key.SetValue(
                WindowsStartupRegistrationService.AppName,
                $"\"{launcher}\"",
                RegistryValueKind.String);

            Assert.True(await service.IsRegisteredAsync());
            await service.SetRegisteredAsync(true);
            Assert.Equal($"\"{launcher}\"", registry.Key.Values[WindowsStartupRegistrationService.AppName]);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StartupRegistrationRejectsMissingAndGenericHostExecutables()
    {
        var registry = new FakeRegistry();
        var missing = new WindowsStartupRegistrationService(registry, () => null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.SetRegisteredAsync(true));

        var generic = new WindowsStartupRegistrationService(registry, () => Path.Combine(Path.GetTempPath(), "dotnet.exe"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => generic.SetRegisteredAsync(true));
        Assert.Contains("generic", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartupRegistrationRequiresTheExactCurrentStringCommand()
    {
        using var fixture = new TempFile();
        var registry = new FakeRegistry();
        var service = new WindowsStartupRegistrationService(registry, () => fixture.Path);
        await service.SetRegisteredAsync(true);

        registry.Key.Values[WindowsStartupRegistrationService.AppName] = "";
        Assert.False(await service.IsRegisteredAsync());

        registry.Key.Values[WindowsStartupRegistrationService.AppName] = 42;
        Assert.False(await service.IsRegisteredAsync());

        registry.Key.Values[WindowsStartupRegistrationService.AppName] = $"\"{fixture.Path}\"";
        registry.Key.Kinds[WindowsStartupRegistrationService.AppName] = RegistryValueKind.ExpandString;
        Assert.False(await service.IsRegisteredAsync());

        registry.Key.Values[WindowsStartupRegistrationService.AppName] = "\"C:\\stale\\Batterio.exe\"";
        registry.Key.Kinds[WindowsStartupRegistrationService.AppName] = RegistryValueKind.String;
        Assert.False(await service.IsRegisteredAsync());
    }

    [Fact]
    public async Task StartupRegistrationBecomesInvalidWhenTheCurrentExecutableDisappears()
    {
        using var fixture = new TempFile();
        var registry = new FakeRegistry();
        var service = new WindowsStartupRegistrationService(registry, () => fixture.Path);
        await service.SetRegisteredAsync(true);

        File.Delete(fixture.Path);

        Assert.False(await service.IsRegisteredAsync());
    }

    [Fact]
    public async Task StartupRegistrationRejectsCommandsOverTheRunKeyLimit()
    {
        var registry = new FakeRegistry();
        var longPath = @"C:\" + new string('a', WindowsStartupRegistrationService.MaximumRunCommandLength) + ".exe";
        var service = new WindowsStartupRegistrationService(registry, () => longPath, _ => true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetRegisteredAsync(true));

        Assert.Contains(WindowsStartupRegistrationService.MaximumRunCommandLength.ToString(), error.Message);
        Assert.Empty(registry.Key.Values);
    }

    [Fact]
    public async Task RegistryReadErrorsAreNotConvertedToUnchecked()
    {
        var registry = new FakeRegistry { ThrowOnOpen = true };
        var service = new WindowsStartupRegistrationService(registry, () => "ignored.exe");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.IsRegisteredAsync());
    }

    [Fact]
    public async Task ValueDisappearingDuringKindLookupIsUnregistered()
    {
        using var fixture = new TempFile();
        var registry = new FakeRegistry();
        var service = new WindowsStartupRegistrationService(registry, () => fixture.Path);
        await service.SetRegisteredAsync(true);
        registry.Key.ValueKindError = new IOException("Value disappeared", 2);

        Assert.False(await service.IsRegisteredAsync());
    }

    [Theory]
    [InlineData(6)] // ERROR_INVALID_HANDLE
    [InlineData(1015)] // ERROR_REGISTRY_CORRUPT
    [InlineData(unchecked((int)0x80131620))] // Generic IOException
    public async Task ValueKindReadFailuresPropagate(int errorCode)
    {
        using var fixture = new TempFile();
        var registry = new FakeRegistry();
        var service = new WindowsStartupRegistrationService(registry, () => fixture.Path);
        await service.SetRegisteredAsync(true);
        var failure = new IOException("Registry query failed", errorCode);
        registry.Key.ValueKindError = failure;

        var actual = await Assert.ThrowsAsync<IOException>(() => service.IsRegisteredAsync());

        Assert.Same(failure, actual);
    }

    [Fact]
    public async Task RefreshRewritesAStaleEntryToTheCurrentExecutable()
    {
        using var fixture = new TempFile();
        var registry = new FakeRegistry();
        var service = new WindowsStartupRegistrationService(registry, () => fixture.Path);
        registry.Key.SetValue(WindowsStartupRegistrationService.AppName, "\"C:\\old\\Batterio.exe\"", RegistryValueKind.String);

        Assert.True(await service.RefreshRegistrationAsync());

        Assert.Equal($"\"{Path.GetFullPath(fixture.Path)}\"", registry.Key.Values[WindowsStartupRegistrationService.AppName]);
        Assert.True(await service.IsRegisteredAsync());
        Assert.False(await service.RefreshRegistrationAsync());
    }

    [Fact]
    public async Task RefreshRewritesAnExpandStringEntryAsString()
    {
        using var fixture = new TempFile();
        var registry = new FakeRegistry();
        var service = new WindowsStartupRegistrationService(registry, () => fixture.Path);
        registry.Key.SetValue(WindowsStartupRegistrationService.AppName, $"\"{Path.GetFullPath(fixture.Path)}\"", RegistryValueKind.ExpandString);

        Assert.True(await service.RefreshRegistrationAsync());

        Assert.Equal(RegistryValueKind.String, registry.Key.Kinds[WindowsStartupRegistrationService.AppName]);
    }

    [Fact]
    public async Task RefreshLeavesStartupOffWhenThereIsNoEntry()
    {
        using var fixture = new TempFile();
        var registry = new FakeRegistry();
        var service = new WindowsStartupRegistrationService(registry, () => fixture.Path);

        Assert.False(await service.RefreshRegistrationAsync());

        Assert.False(registry.Key.Values.ContainsKey(WindowsStartupRegistrationService.AppName));
    }

    private sealed class FakeRegistry : IStartupRegistry
    {
        public FakeKey Key { get; } = new();

        public bool ThrowOnOpen { get; set; }

        public IStartupRegistryKey? OpenCurrentUser(string subKeyPath, bool writable)
        {
            if (ThrowOnOpen) throw new InvalidOperationException("registry read failed");
            return Key;
        }
    }

    private sealed class FakeKey : IStartupRegistryKey
    {
        public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, RegistryValueKind> Kinds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IOException? ValueKindError { get; set; }

        public object? GetValue(string name) => Values.TryGetValue(name, out var value) ? value : null;

        public RegistryValueKind? GetValueKind(string name)
        {
            if (ValueKindError is not null) throw ValueKindError;
            return Kinds.TryGetValue(name, out var kind) ? kind : null;
        }

        public void SetValue(string name, object value, RegistryValueKind valueKind)
        {
            Values[name] = value;
            Kinds[name] = valueKind;
        }

        public void DeleteValue(string name, bool throwOnMissingValue)
        {
            Values.Remove(name);
            Kinds.Remove(name);
        }

        public void Dispose() { }
    }

    private sealed class TempFile : IDisposable
    {
        public TempFile()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Batterio.Core.Tests", Guid.NewGuid() + ".exe");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, "test");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
    }
}

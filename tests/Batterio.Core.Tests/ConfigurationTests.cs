using System.Text.Json;
using Batterio.Core;

namespace Batterio.Core.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public async Task MissingFileIsCreatedWithVersionOneAndEmptyDevices()
    {
        using var fixture = new TempDirectory();
        var path = Path.Combine(fixture.Path, "devices.json");
        var snapshot = await new ConfigurationLoader(path).LoadAsync();

        Assert.Empty(snapshot.Devices);
        Assert.True(File.Exists(path));
        Assert.Contains("\"version\": 1", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DefaultsAndRelativePhotoPathAreApplied()
    {
        using var fixture = new TempDirectory();
        var path = Path.Combine(fixture.Path, "devices.json");
        await File.WriteAllTextAsync(path, """
        {
          "version": 1,
          "devices": [{
            "id": "desk",
            "name": "Desk",
            "photoPath": "photos/desk.png",
            "adapterId": "future.adapter",
            "adapterSettings": {}
          }]
        }
        """);

        var device = (await new ConfigurationLoader(path).LoadAsync()).Devices.Single();

        Assert.True(device.Enabled);
        Assert.Equal(300, device.PollIntervalSeconds);
        Assert.Equal(10, device.TimeoutSeconds);
        Assert.Equal(Path.Combine(fixture.Path, "photos", "desk.png"), device.ResolvePhotoPath(fixture.Path));
    }

    [Theory]
    [InlineData("duplicate", "duplicate")]
    public void DuplicateIdsAreRejected(string firstId, string secondId)
    {
        var errors = ConfigurationLoader.Validate(1, new[]
        {
            Device("first", firstId),
            Device("second", secondId)
        });

        Assert.Contains(errors, error => error.Contains("duplicated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InvalidCommonFieldsAreRejectedTogether()
    {
        var errors = ConfigurationLoader.Validate(99, new[]
        {
            new DeviceConfiguration
            {
                Id = "",
                Name = "",
                AdapterId = "",
                AdapterSettings = JsonDocument.Parse("[]").RootElement,
                PollIntervalSeconds = 4,
                TimeoutSeconds = 5
            }
        });

        Assert.NotEmpty(errors);
        Assert.Contains(errors, error => error.Contains("Unsupported", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("missing id", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("adapterSettings", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("pollIntervalSeconds", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MalformedAndUnsupportedDocumentsAreRejected()
    {
        using var fixture = new TempDirectory();
        var path = Path.Combine(fixture.Path, "devices.json");
        await File.WriteAllTextAsync(path, "{ not json }");
        await Assert.ThrowsAsync<ConfigurationValidationException>(() => new ConfigurationLoader(path).LoadAsync());

        await File.WriteAllTextAsync(path, "{\"version\":2,\"devices\":[]}");
        var error = await Assert.ThrowsAsync<ConfigurationValidationException>(() => new ConfigurationLoader(path).LoadAsync());
        Assert.Contains("Unsupported", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static DeviceConfiguration Device(string name, string id) => new()
    {
        Id = id,
        Name = name,
        AdapterId = "test",
        AdapterSettings = JsonDocument.Parse("{}").RootElement
    };

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Batterio.Core.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}

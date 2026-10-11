using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Batterio.Startup;

namespace Batterio.Startup.Tests;

public sealed class EmbeddedPayloadTests : IDisposable
{
    private const string RuntimeName = "Batterio.Runtime.exe";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Batterio-payload-" + Guid.NewGuid().ToString("N"));

    public EmbeddedPayloadTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void LauncherWithoutPayloadRunsFromItsOwnFolder()
    {
        var executable = Path.Combine(_directory, "Batterio.App.exe");
        File.WriteAllBytes(executable, new byte[4096]);

        Assert.Null(EmbeddedPayload.TryPrepare(executable, Path.Combine(_directory, "cache"), RuntimeName));
    }

    [Fact]
    public void PayloadIsUnpackedOnceAndReused()
    {
        var executable = WriteBundle("v1", ("runtime.txt", "v1"));
        var cache = Path.Combine(_directory, "cache");

        var first = EmbeddedPayload.TryPrepare(executable, cache, RuntimeName);
        Assert.NotNull(first);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(first, "runtime.txt")));
        Assert.Equal("v1", File.ReadAllText(Path.Combine(first, "Resources", "nested.txt")));

        var marker = Path.Combine(first, "runtime.txt");
        File.WriteAllText(marker, "kept");
        var second = EmbeddedPayload.TryPrepare(executable, cache, RuntimeName);

        Assert.Equal(first, second);
        Assert.Equal("kept", File.ReadAllText(marker));
    }

    [Fact]
    public void IncompleteCopyIsReplaced()
    {
        var executable = WriteBundle("v1", ("runtime.txt", "v1"));
        var cache = Path.Combine(_directory, "cache");
        var first = EmbeddedPayload.TryPrepare(executable, cache, RuntimeName)!;
        File.Delete(Path.Combine(first, RuntimeName));

        var second = EmbeddedPayload.TryPrepare(executable, cache, RuntimeName)!;

        Assert.Equal(first, second);
        Assert.True(File.Exists(Path.Combine(second, RuntimeName)));
    }

    [Fact]
    public void NewVersionRemovesCopiesThatAreNotInUse()
    {
        var cache = Path.Combine(_directory, "cache");
        var old = EmbeddedPayload.TryPrepare(WriteBundle("v1", ("runtime.txt", "v1")), cache, RuntimeName)!;
        var current = EmbeddedPayload.TryPrepare(WriteBundle("v2", ("runtime.txt", "v2")), cache, RuntimeName)!;

        Assert.NotEqual(old, current);
        Assert.False(Directory.Exists(old));
        Assert.Equal([current], Directory.GetDirectories(cache));
    }

    [Fact]
    public void CopyInUseIsKept()
    {
        var cache = Path.Combine(_directory, "cache");
        var old = EmbeddedPayload.TryPrepare(WriteBundle("v1", ("runtime.txt", "v1")), cache, RuntimeName)!;
        using (File.Open(Path.Combine(old, RuntimeName), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            EmbeddedPayload.TryPrepare(WriteBundle("v2", ("runtime.txt", "v2")), cache, RuntimeName);
        }

        Assert.True(File.Exists(Path.Combine(old, RuntimeName)));
    }

    [Fact]
    public void CorruptPayloadIsRejected()
    {
        var executable = WriteBundle("v1", ("runtime.txt", "v1"));
        var bytes = File.ReadAllBytes(executable);
        bytes[bytes.Length - EmbeddedPayload.TrailerLength - 10] ^= 0xFF;
        File.WriteAllBytes(executable, bytes);

        Assert.Throws<InvalidDataException>(() =>
            EmbeddedPayload.TryPrepare(executable, Path.Combine(_directory, "cache"), RuntimeName));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string WriteBundle(string name, params (string Path, string Content)[] files)
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files.Append((RuntimeName, name)).Append(("Resources/nested.txt", name)))
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                writer.Write(content);
            }
        }

        var payload = zip.ToArray();
        var executable = Path.Combine(_directory, name + ".exe");
        using var output = File.Create(executable);
        output.Write(new byte[4096]);
        output.Write(payload);
        output.Write(SHA256.HashData(payload));
        Span<byte> length = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(length, payload.Length);
        output.Write(length);
        output.Write(EmbeddedPayload.Magic);
        return executable;
    }
}

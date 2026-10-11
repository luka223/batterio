using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Batterio.Startup;

/// <summary>
/// Unpacks the application folder appended to a single-file Batterio.exe. The file layout is
/// [launcher image][zip payload][payload SHA-256 (32 bytes)][payload length (int64 LE)][magic (8 bytes)].
/// A launcher without the trailer runs from its own folder, as in the multi-file publish.
/// </summary>
public static class EmbeddedPayload
{
    public const int HashLength = 32;
    public const int TrailerLength = HashLength + sizeof(long) + 8;
    public const string CompleteMarkerName = ".batterio-payload";
    private const string ExtractingInfix = ".extracting-";
    private const string DeletingInfix = ".deleting-";
    private static readonly TimeSpan AbandonedExtractionAge = TimeSpan.FromDays(1);

    public static ReadOnlySpan<byte> Magic => "BATTERIO"u8;

    public static string DefaultCacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Batterio", "Runtime");

    /// <summary>
    /// Returns the folder holding the unpacked payload of <paramref name="executablePath"/>,
    /// extracting it on first use, or null when the executable carries no payload.
    /// </summary>
    public static string? TryPrepare(string executablePath, string cacheRoot, string runtimeExecutableName)
    {
        using var file = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (!TryReadTrailer(file, out var expectedHash, out var payloadOffset, out var payloadLength))
        {
            return null;
        }

        var id = Convert.ToHexStringLower(expectedHash, 0, 8);
        cacheRoot = Path.GetFullPath(cacheRoot);
        var target = Path.Combine(cacheRoot, id);
        if (!IsComplete(target, id, runtimeExecutableName))
        {
            Extract(file, payloadOffset, payloadLength, expectedHash, cacheRoot, id, target, runtimeExecutableName);
        }

        RemoveStaleCopies(cacheRoot, id);
        return target;
    }

    private static void Extract(
        FileStream file,
        long payloadOffset,
        long payloadLength,
        byte[] expectedHash,
        string cacheRoot,
        string id,
        string target,
        string runtimeExecutableName)
    {
        using (var payload = new PayloadStream(file, payloadOffset, payloadLength))
        {
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), expectedHash))
            {
                throw new InvalidDataException("The embedded Batterio payload is corrupt.");
            }
        }

        Directory.CreateDirectory(cacheRoot);
        var staging = Path.Combine(cacheRoot, id + ExtractingInfix + Guid.NewGuid().ToString("N"));
        try
        {
            using (var payload = new PayloadStream(file, payloadOffset, payloadLength))
            using (var archive = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                archive.ExtractToDirectory(staging);
            }

            // Written last so a folder with the marker is known to hold every payload file.
            File.WriteAllText(Path.Combine(staging, CompleteMarkerName), id);
            if (Directory.Exists(target))
            {
                // An incomplete or damaged copy; replace it unless something still runs from it.
                TryRemove(target);
            }

            try
            {
                Directory.Move(staging, target);
            }
            catch (IOException) when (IsComplete(target, id, runtimeExecutableName))
            {
                // Another launcher finished extracting the same payload first.
            }
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                TryDelete(staging);
            }
        }

        if (!IsComplete(target, id, runtimeExecutableName))
        {
            throw new IOException("The embedded Batterio payload could not be unpacked.");
        }
    }

    private static bool TryReadTrailer(FileStream file, out byte[] hash, out long payloadOffset, out long payloadLength)
    {
        hash = [];
        payloadOffset = 0;
        payloadLength = 0;
        if (file.Length < TrailerLength)
        {
            return false;
        }

        Span<byte> trailer = stackalloc byte[TrailerLength];
        file.Position = file.Length - TrailerLength;
        file.ReadExactly(trailer);
        if (!trailer[^Magic.Length..].SequenceEqual(Magic))
        {
            return false;
        }

        payloadLength = BinaryPrimitives.ReadInt64LittleEndian(trailer.Slice(HashLength, sizeof(long)));
        payloadOffset = file.Length - TrailerLength - payloadLength;
        if (payloadLength <= 0 || payloadOffset <= 0)
        {
            throw new InvalidDataException("The embedded Batterio payload has an invalid length.");
        }

        hash = trailer[..HashLength].ToArray();
        return true;
    }

    private static bool IsComplete(string directory, string id, string runtimeExecutableName)
    {
        try
        {
            var marker = Path.Combine(directory, CompleteMarkerName);
            return File.Exists(marker) &&
                   File.Exists(Path.Combine(directory, runtimeExecutableName)) &&
                   File.ReadAllText(marker).Trim() == id;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes copies left by other Batterio.exe versions. A folder is first renamed, which
    /// Windows refuses while a process runs from it, so a running copy is never touched.
    /// </summary>
    private static void RemoveStaleCopies(string cacheRoot, string currentId)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(cacheRoot))
            {
                var name = Path.GetFileName(directory);
                if (name.Equals(currentId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (name.Contains(DeletingInfix, StringComparison.Ordinal))
                {
                    TryDelete(directory);
                }
                else if (name.Contains(ExtractingInfix, StringComparison.Ordinal))
                {
                    // Another launcher may be extracting right now; only clear abandoned attempts.
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) > AbandonedExtractionAge)
                    {
                        TryDelete(directory);
                    }
                }
                else
                {
                    TryRemove(directory);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best effort and must never block startup.
        }
    }

    private static void TryRemove(string directory)
    {
        var doomed = directory + DeletingInfix + Guid.NewGuid().ToString("N");
        try
        {
            Directory.Move(directory, doomed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        TryDelete(doomed);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Retried on a later start.
        }
    }

    /// <summary>A read-only window over the payload part of the executable.</summary>
    private sealed class PayloadStream(FileStream file, long offset, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = value is >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var remaining = length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            if (buffer.Length > remaining)
            {
                buffer = buffer[..(int)remaining];
            }

            file.Position = offset + _position;
            var read = file.Read(buffer);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

using System.Security.Cryptography;

namespace Batterio.Core.Updates;

/// <summary>Downloads a release executable and verifies it before it may replace the installed one.</summary>
public sealed class UpdateDownloader(HttpClient http)
{
    private const int BufferSize = 81920;
    private static ReadOnlySpan<byte> PayloadMagic => "BATTERIO"u8;

    /// <summary>How long a download may go without receiving data before it is abandoned.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Downloads <paramref name="release"/> to <paramref name="destination"/>. Progress is reported as 0..1.
    /// The file only appears at <paramref name="destination"/> once it has been fully verified.
    /// </summary>
    public async Task DownloadAsync(
        UpdateRelease release,
        string destination,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var partial = destination + ".partial";
        try
        {
            TryDelete(destination);
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stall.CancelAfter(StallTimeout);
            using var response = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, stall.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException($"GitHub returned {(int)response.StatusCode} while downloading the update.");
            }

            var expectedLength = release.Size > 0 ? release.Size : response.Content.Headers.ContentLength ?? 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    int read;
                    try
                    {
                        read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new UpdateException("The download stopped receiving data. Check your internet connection and try again.");
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    if (expectedLength > 0)
                    {
                        progress?.Report(Math.Min(1d, (double)received / expectedLength));
                    }
                }
            }

            Verify(partial, received, expectedLength, release.Sha256, Convert.ToHexStringLower(hash.GetHashAndReset()));
            File.Move(partial, destination, overwrite: true);
            progress?.Report(1d);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryDelete(partial);
            throw new UpdateException("The download timed out. Check your internet connection and try again.");
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static void Verify(string path, long received, long expectedLength, string? expectedSha256, string actualSha256)
    {
        if (expectedLength > 0 && received != expectedLength)
        {
            throw new UpdateException("The download was incomplete. Try again.");
        }

        if (expectedSha256 is not null && !string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("The downloaded file does not match the release checksum. Try again.");
        }

        if (!IsBatterioExecutable(path))
        {
            throw new UpdateException("The downloaded file is not a single-file Batterio.exe.");
        }
    }

    /// <summary>True when the file is a Windows executable carrying an embedded Batterio payload.</summary>
    public static bool IsBatterioExecutable(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length < 64 + PayloadMagic.Length)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[2];
        file.ReadExactly(header);
        if (header[0] != (byte)'M' || header[1] != (byte)'Z')
        {
            return false;
        }

        Span<byte> trailer = stackalloc byte[PayloadMagic.Length];
        file.Position = file.Length - trailer.Length;
        file.ReadExactly(trailer);
        return trailer.SequenceEqual(PayloadMagic);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

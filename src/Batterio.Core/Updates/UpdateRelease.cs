using System.Text.Json;

namespace Batterio.Core.Updates;

/// <summary>A published Batterio release that carries the single-file executable.</summary>
public sealed record UpdateRelease(Version Version, string Tag, Uri DownloadUrl, long Size, string? Sha256);

public interface IUpdateReleaseSource
{
    /// <summary>Returns the latest published release, or null when none carries a usable executable.</summary>
    Task<UpdateRelease?> GetLatestAsync(CancellationToken cancellationToken = default);
}

public static class UpdateVersion
{
    /// <summary>Parses a release tag or informational version such as "v1.2.0" or "1.2.0+abc".</summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var end = value.IndexOfAny(['+', '-', ' ']);
        if (end >= 0)
        {
            value = value[..end];
        }

        if (!Version.TryParse(value, out var parsed))
        {
            return false;
        }

        // 1.2 and 1.2.0 must compare equal.
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
        return true;
    }
}

/// <summary>Reads releases from the public GitHub releases API.</summary>
public sealed class GitHubUpdateReleaseSource(HttpClient http, string repository, string assetName) : IUpdateReleaseSource
{
    public const string DefaultRepository = "luka223/batterio";
    public const string DefaultAssetName = "Batterio.exe";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public async Task<UpdateRelease?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            return await GetLatestCoreAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("GitHub did not respond in time. Try again.");
        }
    }

    private async Task<UpdateRelease?> GetLatestCoreAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if ((int)response.StatusCode is 403 or 429)
        {
            throw new UpdateException("GitHub is limiting update checks right now. Try again later.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException($"GitHub returned {(int)response.StatusCode} while checking for updates.");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Parse(json, assetName);
    }

    public static UpdateRelease? Parse(string json, string assetName)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (IsTrue(root, "draft") || IsTrue(root, "prerelease"))
        {
            return null;
        }

        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
        if (!UpdateVersion.TryParse(tag, out var version) ||
            !root.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (!string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var downloadUrl) || downloadUrl.Scheme != Uri.UriSchemeHttps)
            {
                return null;
            }

            var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var length) ? length : 0;
            string? sha256 = null;
            if (asset.TryGetProperty("digest", out var digestElement) &&
                digestElement.GetString() is { } digest &&
                digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                sha256 = digest["sha256:".Length..].Trim().ToLowerInvariant();
            }

            return new UpdateRelease(version, tag!, downloadUrl, size, sha256);
        }

        return null;
    }

    private static bool IsTrue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.True;
}

/// <summary>An update failure whose message is safe to show to the user.</summary>
public sealed class UpdateException(string message, Exception? innerException = null) : Exception(message, innerException);

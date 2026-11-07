using System.Net;
using System.Security.Cryptography;
using System.Text;
using Batterio.Core.Abstractions;
using Batterio.Core.Updates;

namespace Batterio.Core.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "batterio-update-tests-" + Guid.NewGuid().ToString("N"));

    public UpdateTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("1.2.0", 1, 2, 0)]
    [InlineData("v1.2", 1, 2, 0)]
    [InlineData("1.1.0+e00461c6ddf", 1, 1, 0)]
    [InlineData("2.0.1-beta", 2, 0, 1)]
    public void VersionsParseFromTagsAndInformationalVersions(string text, int major, int minor, int build)
    {
        Assert.True(UpdateVersion.TryParse(text, out var version));
        Assert.Equal(new Version(major, minor, build, 0), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    public void InvalidVersionsAreRejected(string? text) => Assert.False(UpdateVersion.TryParse(text, out _));

    [Fact]
    public void SameVersionWithAndWithoutPatchIsNotNewer()
    {
        UpdateVersion.TryParse("1.1", out var tag);
        UpdateVersion.TryParse("1.1.0+abc", out var current);
        Assert.False(tag > current);
    }

    [Fact]
    public void ReleaseJsonYieldsTheExecutableAssetAndDigest()
    {
        var release = GitHubUpdateReleaseSource.Parse(ReleaseJson("1.2.0", "sha256:ABCDEF"), "Batterio.exe");
        Assert.NotNull(release);
        Assert.Equal(new Version(1, 2, 0, 0), release.Version);
        Assert.Equal("1.2.0", release.Tag);
        Assert.Equal(new Uri("https://github.com/luka223/batterio/releases/download/1.2.0/Batterio.exe"), release.DownloadUrl);
        Assert.Equal(1234, release.Size);
        Assert.Equal("abcdef", release.Sha256);
    }

    [Fact]
    public void ReleaseWithoutTheExecutableOrMarkedPrereleaseIsIgnored()
    {
        Assert.Null(GitHubUpdateReleaseSource.Parse(ReleaseJson("1.2.0", null), "Other.exe"));
        Assert.Null(GitHubUpdateReleaseSource.Parse(ReleaseJson("1.2.0", null, prerelease: true), "Batterio.exe"));
        Assert.Null(GitHubUpdateReleaseSource.Parse(ReleaseJson("not-a-version", null), "Batterio.exe"));
    }

    [Fact]
    public async Task ReleaseSourceReadsTheLatestReleaseEndpoint()
    {
        var handler = new FakeHandler(request =>
        {
            Assert.Equal("https://api.github.com/repos/owner/repo/releases/latest", request.RequestUri!.ToString());
            return Json(ReleaseJson("3.0.0", null));
        });
        var source = new GitHubUpdateReleaseSource(new HttpClient(handler), "owner/repo", "Batterio.exe");
        var release = await source.GetLatestAsync();
        Assert.Equal(new Version(3, 0, 0, 0), release!.Version);
    }

    [Fact]
    public async Task RateLimitedCheckReportsAReadableError()
    {
        var source = new GitHubUpdateReleaseSource(
            new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))), "o/r", "Batterio.exe");
        var exception = await Assert.ThrowsAsync<UpdateException>(() => source.GetLatestAsync());
        Assert.Contains("limiting", exception.Message);
    }

    [Fact]
    public async Task DownloadVerifiesAndReportsProgress()
    {
        var payload = Executable(200_000);
        var destination = Path.Combine(_directory, "Batterio.exe.download");
        var progress = new List<double>();
        var downloader = new UpdateDownloader(new HttpClient(new FakeHandler(_ => Bytes(payload))));
        await downloader.DownloadAsync(Release(payload), destination, new InlineProgress(progress.Add));

        Assert.Equal(payload, File.ReadAllBytes(destination));
        Assert.False(File.Exists(destination + ".partial"));
        Assert.Equal(1d, progress[^1]);
        Assert.True(progress.Count > 2);
        Assert.Equal(progress.OrderBy(value => value), progress);
    }

    [Fact]
    public async Task DownloadWithWrongChecksumIsDiscarded()
    {
        var payload = Executable(10_000);
        var destination = Path.Combine(_directory, "Batterio.exe.download");
        var downloader = new UpdateDownloader(new HttpClient(new FakeHandler(_ => Bytes(payload))));
        var release = Release(payload) with { Sha256 = new string('0', 64) };
        var exception = await Assert.ThrowsAsync<UpdateException>(() => downloader.DownloadAsync(release, destination));
        Assert.Contains("checksum", exception.Message);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".partial"));
    }

    [Fact]
    public async Task DownloadThatIsNotABatterioExecutableIsDiscarded()
    {
        var payload = new byte[5_000];
        var destination = Path.Combine(_directory, "Batterio.exe.download");
        var downloader = new UpdateDownloader(new HttpClient(new FakeHandler(_ => Bytes(payload))));
        await Assert.ThrowsAsync<UpdateException>(() => downloader.DownloadAsync(Release(payload), destination));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task TruncatedDownloadIsDiscarded()
    {
        var payload = Executable(10_000);
        var destination = Path.Combine(_directory, "Batterio.exe.download");
        var downloader = new UpdateDownloader(new HttpClient(new FakeHandler(_ => Bytes(payload))));
        var release = Release(payload) with { Size = payload.Length + 10, Sha256 = null };
        var exception = await Assert.ThrowsAsync<UpdateException>(() => downloader.DownloadAsync(release, destination));
        Assert.Contains("incomplete", exception.Message);
    }

    [Fact]
    public void SwapMovesTheOldExecutableAsideAndRevertRestoresIt()
    {
        var executable = Path.Combine(_directory, "Batterio.exe");
        var download = UpdateFiles.DownloadPathFor(executable);
        File.WriteAllText(executable, "old");
        File.WriteAllText(download, "new");
        File.WriteAllText(UpdateFiles.OldPathFor(executable), "older");

        UpdateFiles.Swap(executable, download);
        Assert.Equal("new", File.ReadAllText(executable));
        Assert.Equal("old", File.ReadAllText(UpdateFiles.OldPathFor(executable)));
        Assert.False(File.Exists(download));

        UpdateFiles.Revert(executable, download);
        Assert.Equal("old", File.ReadAllText(executable));
        Assert.Equal("new", File.ReadAllText(download));
        Assert.False(File.Exists(UpdateFiles.OldPathFor(executable)));
    }

    [Fact]
    public async Task CheckReportsUpToDateWhenTheLatestReleaseIsNotNewer()
    {
        var coordinator = Coordinator(new FakeSource(new UpdateRelease(new Version(1, 1, 0, 0), "1.1.0", new Uri("https://x/a"), 1, null)), out _, out _);
        var snapshot = await coordinator.CheckAsync();
        Assert.Equal(UpdateStatus.UpToDate, snapshot.Status);
        Assert.Null(snapshot.Release);
    }

    [Fact]
    public async Task CheckFailureKeepsThePreviousStateAndExplainsWhy()
    {
        var coordinator = Coordinator(new FakeSource(new HttpRequestException("offline")), out _, out _);
        var snapshot = await coordinator.CheckAsync();
        Assert.Equal(UpdateStatus.Idle, snapshot.Status);
        Assert.Contains("internet", snapshot.Error);
    }

    [Fact]
    public async Task CheckDownloadAndInstallReplaceTheExecutableAndRestart()
    {
        var payload = Executable(50_000);
        var coordinator = Coordinator(new FakeSource(Release(payload)), out var executable, out var restarter, payload);
        var states = new List<UpdateStatus>();
        coordinator.SnapshotChanged += (_, snapshot) => states.Add(snapshot.Status);

        Assert.Equal(UpdateStatus.Available, (await coordinator.CheckAsync()).Status);
        await coordinator.DownloadAsync();
        Assert.Equal(UpdateStatus.ReadyToInstall, coordinator.Current.Status);
        Assert.Equal(1d, coordinator.Current.Progress);
        Assert.Equal("old", File.ReadAllText(executable));

        await coordinator.InstallAsync();
        Assert.Equal(payload, File.ReadAllBytes(executable));
        Assert.Equal(executable, restarter.Started);
        Assert.True(restarter.Exited);
        Assert.Equal(
            [UpdateStatus.Checking, UpdateStatus.Available, UpdateStatus.Downloading, UpdateStatus.ReadyToInstall, UpdateStatus.Installing],
            states.Distinct());
    }

    [Fact]
    public async Task FailedRestartRestoresThePreviousExecutable()
    {
        var payload = Executable(5_000);
        var coordinator = Coordinator(new FakeSource(Release(payload)), out var executable, out var restarter, payload);
        restarter.Fail = true;
        await coordinator.CheckAsync();
        await coordinator.DownloadAsync();
        await coordinator.InstallAsync();

        Assert.Equal("old", File.ReadAllText(executable));
        Assert.False(restarter.Exited);
        Assert.Equal(UpdateStatus.ReadyToInstall, coordinator.Current.Status);
        Assert.NotNull(coordinator.Current.Error);
    }

    [Fact]
    public async Task WithoutAnUpdatableExecutableDownloadIsNotOffered()
    {
        var payload = Executable(5_000);
        var coordinator = new UpdateCoordinator(new FakeSource(Release(payload)),
            new UpdateDownloader(new HttpClient(new FakeHandler(_ => Bytes(payload)))),
            new FakeRestarter(), new Version(1, 1, 0, 0), executablePath: null, new NullBatterioLog());
        var snapshot = await coordinator.CheckAsync();
        Assert.Equal(UpdateStatus.Available, snapshot.Status);
        Assert.False(snapshot.CanInstall);
        await coordinator.DownloadAsync();
        Assert.Equal(UpdateStatus.Available, coordinator.Current.Status);
    }

    [Fact]
    public async Task FirstCheckRemovesLeftoversOfEarlierUpdates()
    {
        var coordinator = Coordinator(new FakeSource((UpdateRelease?)null), out var executable, out _);
        File.WriteAllText(UpdateFiles.OldPathFor(executable), "x");
        File.WriteAllText(UpdateFiles.DownloadPathFor(executable), "x");
        await coordinator.CheckAsync();
        Assert.False(File.Exists(UpdateFiles.OldPathFor(executable)));
        Assert.False(File.Exists(UpdateFiles.DownloadPathFor(executable)));
        Assert.True(File.Exists(executable));
    }

    private UpdateCoordinator Coordinator(
        IUpdateReleaseSource source, out string executable, out FakeRestarter restarter, byte[]? payload = null)
    {
        executable = Path.Combine(_directory, "Batterio.exe");
        File.WriteAllText(executable, "old");
        restarter = new FakeRestarter();
        var bytes = payload ?? [];
        return new UpdateCoordinator(source,
            new UpdateDownloader(new HttpClient(new FakeHandler(_ => Bytes(bytes)))),
            restarter, new Version(1, 1, 0, 0), executable, new NullBatterioLog());
    }

    private static UpdateRelease Release(byte[] payload) => new(
        new Version(1, 2, 0, 0), "1.2.0", new Uri("https://github.com/o/r/releases/download/1.2.0/Batterio.exe"),
        payload.Length, Convert.ToHexStringLower(SHA256.HashData(payload)));

    private static byte[] Executable(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        "BATTERIO"u8.CopyTo(bytes.AsSpan(length - 8));
        return bytes;
    }

    private static string ReleaseJson(string tag, string? digest, bool prerelease = false) => $$"""
        {
          "tag_name": "{{tag}}",
          "draft": false,
          "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": [
            {
              "name": "Batterio.exe",
              "size": 1234,
              {{(digest is null ? "" : $"\"digest\": \"{digest}\",")}}
              "browser_download_url": "https://github.com/luka223/batterio/releases/download/{{tag}}/Batterio.exe"
            }
          ]
        }
        """;

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ChunkedContent(bytes) };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    /// <summary>Hands out the body in small reads, like a network stream.</summary>
    private sealed class ChunkedContent(byte[] bytes) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            for (var offset = 0; offset < bytes.Length; offset += 4096)
            {
                await stream.WriteAsync(bytes.AsMemory(offset, Math.Min(4096, bytes.Length - offset)));
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = bytes.Length;
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new SmallReadStream(new MemoryStream(bytes)));
    }

    private sealed class SmallReadStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 4096));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Math.Min(buffer.Length, 4096)], cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class FakeSource : IUpdateReleaseSource
    {
        private readonly UpdateRelease? _release;
        private readonly Exception? _failure;
        public FakeSource(UpdateRelease? release) => _release = release;
        public FakeSource(Exception failure) => _failure = failure;
        public Task<UpdateRelease?> GetLatestAsync(CancellationToken cancellationToken = default) =>
            _failure is null ? Task.FromResult(_release) : Task.FromException<UpdateRelease?>(_failure);
    }

    private sealed class FakeRestarter : IUpdateRestarter
    {
        public bool Fail;
        public string? Started;
        public bool Exited;
        public void Start(string executablePath)
        {
            if (Fail) throw new InvalidOperationException("cannot start");
            Started = executablePath;
        }

        public Task ExitAsync()
        {
            Exited = true;
            return Task.CompletedTask;
        }
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}

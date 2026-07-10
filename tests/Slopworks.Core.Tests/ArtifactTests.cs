using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Slopworks.Core.Artifacts;
using Slopworks.Core.Config;
using Xunit;

namespace Slopworks.Core.Tests;

public class GlobTests
{
    [Theory]
    [InlineData("podman-5.2.0-x86_64.tar.gz", "*-x86_64.tar.gz", true)]
    [InlineData("podman-5.2.0-aarch64.tar.gz", "*-x86_64.tar.gz", false)]
    [InlineData("Anything.ZIP", "*.zip", true)]
    [InlineData("file.tar.gz.sig", "*.tar.gz", false)]
    [InlineData("v1.2.3", "v?.?.?", true)]
    public void IsMatch_BehavesLikeShellGlob(string text, string pattern, bool expected)
        => Assert.Equal(expected, Glob.IsMatch(text, pattern));
}

public class ChecksumFileTests
{
    private const string Sha256Sums = """
        0e5f4a3f4114e468d5bb32c1b450a17b04ba18756f7a3ffdac8dd825c27dbf78 *ubuntu-noble-wsl-amd64-wsl.rootfs.tar.gz
        aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  other-file.img
        """;

    [Fact]
    public void FindSha256_MatchesFileNameIgnoringBinaryMarker()
    {
        var hash = ChecksumFile.FindSha256(Sha256Sums, "ubuntu-noble-wsl-amd64-wsl.rootfs.tar.gz");

        Assert.Equal("0e5f4a3f4114e468d5bb32c1b450a17b04ba18756f7a3ffdac8dd825c27dbf78", hash);
    }

    [Fact]
    public void FindSha256_ReturnsNullForUnknownFile()
        => Assert.Null(ChecksumFile.FindSha256(Sha256Sums, "nope.tar.gz"));
}

public class ArtifactResolverTests
{
    private const string GitHubLatestJson = """
        {
          "tag_name": "v5.2.0",
          "assets": [
            { "name": "tool-5.2.0-aarch64.tar.gz", "browser_download_url": "https://example.com/dl/aarch64.tar.gz" },
            { "name": "tool-5.2.0-x86_64.tar.gz", "browser_download_url": "https://example.com/dl/x86_64.tar.gz" }
          ]
        }
        """;

    [Fact]
    public async Task ExplicitUrl_WinsAndFetchesChecksum()
    {
        var handler = new FakeHttpHandler(request =>
            request.RequestUri!.ToString().EndsWith("SHA256SUMS")
                ? Text("abc123".PadRight(64, '0') + "  rootfs.tar.gz")
                : throw new InvalidOperationException($"Unexpected request {request.RequestUri}"));
        var resolver = BuildResolver(handler, new ArtifactSource
        {
            Url = "https://mirror.example.com/images/rootfs.tar.gz",
            ChecksumUrl = "https://mirror.example.com/images/SHA256SUMS",
        }, out _);

        var resolved = await resolver.ResolveAsync("rootfs", CancellationToken.None);

        Assert.Equal("https://mirror.example.com/images/rootfs.tar.gz", resolved.Url);
        Assert.Equal("rootfs.tar.gz", resolved.FileName);
        Assert.StartsWith("abc123", resolved.Sha256);
    }

    [Fact]
    public async Task GitHubSource_ResolvesLatestReleaseAssetByPattern()
    {
        var handler = new FakeHttpHandler(request =>
        {
            Assert.Equal("https://api.github.com/repos/acme/tool/releases/latest", request.RequestUri!.ToString());
            return Text(GitHubLatestJson);
        });
        var resolver = BuildResolver(handler, new ArtifactSource
        {
            GitHub = new GitHubSource { Repo = "acme/tool", AssetPattern = "*-x86_64.tar.gz" },
        }, out _);

        var resolved = await resolver.ResolveAsync("rootfs", CancellationToken.None);

        Assert.Equal("https://example.com/dl/x86_64.tar.gz", resolved.Url);
        Assert.Equal("tool-5.2.0-x86_64.tar.gz", resolved.FileName);
        Assert.Contains("acme/tool@v5.2.0", resolved.Source);
    }

    [Fact]
    public async Task GitHubSource_SecondResolveIsServedFromJournalCache()
    {
        var calls = 0;
        var handler = new FakeHttpHandler(_ =>
        {
            calls++;
            return Text(GitHubLatestJson);
        });
        var resolver = BuildResolver(handler, new ArtifactSource
        {
            GitHub = new GitHubSource { Repo = "acme/tool", AssetPattern = "*-x86_64.tar.gz" },
        }, out var journal);

        await resolver.ResolveAsync("rootfs", CancellationToken.None);
        var second = await resolver.ResolveAsync("rootfs", CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal("cached", second.Source);
        Assert.NotNull(journal.Data.ResolvedArtifacts["rootfs"]);
    }

    [Fact]
    public async Task GitHubSource_NoMatchingAsset_Throws()
    {
        var resolver = BuildResolver(new FakeHttpHandler(_ => Text(GitHubLatestJson)), new ArtifactSource
        {
            GitHub = new GitHubSource { Repo = "acme/tool", AssetPattern = "*.msi" },
        }, out _);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync("rootfs", CancellationToken.None));
        Assert.Contains("*.msi", ex.Message);
    }

    [Fact]
    public async Task UnknownArtifactKey_Throws()
    {
        var resolver = BuildResolver(new FakeHttpHandler(_ => Text("")), new ArtifactSource { Url = "https://x/y.z" }, out _);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync("ghost", CancellationToken.None));
    }

    private static ArtifactResolver BuildResolver(FakeHttpHandler handler, ArtifactSource source, out InMemoryJournal journal)
    {
        var config = new SlopworksConfig { Artifacts = new() { ["rootfs"] = source } };
        journal = new InMemoryJournal();
        return new ArtifactResolver(config, journal, new HttpClient(handler), NullLogger.Instance);
    }

    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8),
    };
}

public class DownloaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("slopworks-dl-").FullName;

    [Fact]
    public async Task Download_WritesFileAndVerifiedMarker()
    {
        var payload = "hello slopworks"u8.ToArray();
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload));
        var downloader = new Downloader(new HttpClient(new FakeHttpHandler(_ => Bytes(payload))));
        var dest = Path.Combine(_dir, "file.bin");

        await downloader.DownloadAsync("https://x/file.bin", dest, expected, null, CancellationToken.None);

        Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
        Assert.Equal(expected, (await File.ReadAllTextAsync(Downloader.MarkerPath(dest))).Trim());
        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task Download_ChecksumMismatch_DeletesFileAndThrows()
    {
        var downloader = new Downloader(new HttpClient(new FakeHttpHandler(_ => Bytes("corrupted"u8.ToArray()))));
        var dest = Path.Combine(_dir, "file.bin");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            downloader.DownloadAsync("https://x/file.bin", dest, new string('0', 64), null, CancellationToken.None));

        Assert.Contains("mismatch", ex.Message);
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(Downloader.MarkerPath(dest)));
    }

    [Fact]
    public async Task Download_ResumesPartialFileWithRangeRequest()
    {
        var full = "0123456789"u8.ToArray();
        var dest = Path.Combine(_dir, "file.bin");
        await File.WriteAllBytesAsync(dest + ".part", full[..4]);

        RangeInfo? seenRange = null;
        var handler = new FakeHttpHandler(request =>
        {
            var from = request.Headers.Range?.Ranges.First().From;
            seenRange = new RangeInfo(from);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(full[(int)(from ?? 0)..]),
            };
            return response;
        });

        await new Downloader(new HttpClient(handler))
            .DownloadAsync("https://x/file.bin", dest, null, null, CancellationToken.None);

        Assert.Equal(4, seenRange!.From);
        Assert.Equal(full, await File.ReadAllBytesAsync(dest));
    }

    [Fact]
    public async Task Download_RestartsWhenServerIgnoresRange()
    {
        var full = "0123456789"u8.ToArray();
        var dest = Path.Combine(_dir, "file.bin");
        await File.WriteAllBytesAsync(dest + ".part", "GARBAGE"u8.ToArray());

        var handler = new FakeHttpHandler(_ => Bytes(full)); // plain 200, no range support

        await new Downloader(new HttpClient(handler))
            .DownloadAsync("https://x/file.bin", dest, null, null, CancellationToken.None);

        Assert.Equal(full, await File.ReadAllBytesAsync(dest));
    }

    private sealed record RangeInfo(long? From);

    private static HttpResponseMessage Bytes(byte[] payload) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(payload),
    };

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}

internal sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(respond(request));
}

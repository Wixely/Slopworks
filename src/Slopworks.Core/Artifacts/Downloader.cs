using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Slopworks.Core.Artifacts;

/// <summary>
/// Resumable downloader: partial content goes to "&lt;dest&gt;.part" and resumes with a Range
/// request; on completion the file is verified against the expected SHA-256 (when known)
/// and a "&lt;dest&gt;.sha256.ok" sidecar marker records the verified hash so detection
/// never has to rehash multi-GB files.
/// </summary>
public sealed class Downloader(HttpClient http)
{
    private const int ProgressIntervalBytes = 16 * 1024 * 1024;

    public static string MarkerPath(string destination) => destination + ".sha256.ok";

    public async Task DownloadAsync(
        string url, string destination, string? expectedSha256,
        IProgress<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var part = destination + ".part";
        var existing = File.Exists(part) ? new FileInfo(part).Length : 0L;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var resuming = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (existing > 0 && !resuming)
        {
            progress?.Report("Server does not support resume; restarting download.");
            existing = 0;
        }

        var totalBytes = (response.Content.Headers.ContentLength ?? 0) + (resuming ? existing : 0);
        progress?.Report(resuming
            ? $"Resuming download at {existing / 1024 / 1024} MB: {url}"
            : $"Downloading {(totalBytes > 0 ? totalBytes / 1024 / 1024 + " MB" : "unknown size")}: {url}");

        await using (var file = new FileStream(part, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write))
        await using (var body = await response.Content.ReadAsStreamAsync(ct))
        {
            var buffer = new byte[81920];
            long written = existing, lastReport = existing;
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
                if (written - lastReport >= ProgressIntervalBytes)
                {
                    lastReport = written;
                    progress?.Report(totalBytes > 0
                        ? $"  {written / 1024 / 1024} / {totalBytes / 1024 / 1024} MB"
                        : $"  {written / 1024 / 1024} MB");
                }
            }
        }

        File.Move(part, destination, overwrite: true);
        await VerifyAsync(destination, expectedSha256, progress, ct);
    }

    /// <summary>
    /// Hashes the file, compares against the expected value when one exists, and writes the
    /// sidecar marker. A mismatch deletes the file (it is garbage) and throws.
    /// </summary>
    public static async Task VerifyAsync(
        string destination, string? expectedSha256, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Verifying SHA-256…");
        var actual = await ComputeSha256Async(destination, ct);

        if (expectedSha256 is not null && !string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destination);
            throw new InvalidOperationException(
                $"Checksum mismatch for {Path.GetFileName(destination)}: expected {expectedSha256}, got {actual}. " +
                "The corrupt file was deleted; run again to re-download.");
        }

        await File.WriteAllTextAsync(MarkerPath(destination),
            expectedSha256 is null ? $"{actual} (unverified: no upstream checksum available)" : actual, ct);
        progress?.Report(expectedSha256 is null
            ? $"Hashed {actual[..12]}… (no upstream checksum to compare against)"
            : "Checksum verified.");
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }
}

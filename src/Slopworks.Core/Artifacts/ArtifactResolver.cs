using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Slopworks.Core.Config;
using Slopworks.Core.State;

namespace Slopworks.Core.Artifacts;

public sealed record ResolvedArtifact(string Url, string FileName, string? Sha256, string Source);

/// <summary>
/// Turns a config artifact key into a concrete download URL + expected checksum.
/// Resolution order: explicit url override wins; otherwise a GitHub repo's latest release
/// is queried and the asset pattern matched. GitHub resolutions are cached in the journal
/// for 24 h so detection stays offline.
/// </summary>
public interface IArtifactResolver
{
    Task<ResolvedArtifact> ResolveAsync(string artifactKey, CancellationToken ct);
}

public sealed class ArtifactResolver(
    SlopworksConfig config,
    IStateJournal journal,
    HttpClient http,
    ILogger logger) : IArtifactResolver
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    public async Task<ResolvedArtifact> ResolveAsync(string artifactKey, CancellationToken ct)
    {
        if (!config.Artifacts.TryGetValue(artifactKey, out var source))
            throw new InvalidOperationException($"No artifact source configured for '{artifactKey}'.");

        ResolvedArtifact resolved;
        if (source.Url is { } url)
        {
            var fileName = FileNameFromUrl(url);
            var sha256 = source.Sha256;
            if (sha256 is null && source.ChecksumUrl is { } checksumUrl)
                sha256 = await FetchChecksumAsync(checksumUrl, fileName, ct);
            resolved = new ResolvedArtifact(url, fileName, sha256, "explicit-url");
        }
        else if (source.GitHub is { } github)
        {
            if (TryGetCached(artifactKey, out var cached))
                return cached;
            resolved = await ResolveGitHubLatestAsync(github, ct);
        }
        else
        {
            throw new InvalidOperationException($"Artifact '{artifactKey}' has neither a url nor a github source.");
        }

        journal.Data.ResolvedArtifacts[artifactKey] = new ResolvedArtifactEntry
        {
            Url = resolved.Url,
            Sha256 = resolved.Sha256,
            FileName = resolved.FileName,
            ResolvedAt = DateTimeOffset.UtcNow,
        };
        await journal.SaveAsync(ct);

        return resolved;
    }

    private bool TryGetCached(string artifactKey, out ResolvedArtifact cached)
    {
        cached = null!;
        if (!journal.Data.ResolvedArtifacts.TryGetValue(artifactKey, out var entry))
            return false;
        if (DateTimeOffset.UtcNow - entry.ResolvedAt > CacheTtl)
            return false;

        cached = new ResolvedArtifact(entry.Url, entry.FileName, entry.Sha256, "cached");
        return true;
    }

    private async Task<ResolvedArtifact> ResolveGitHubLatestAsync(GitHubSource github, CancellationToken ct)
    {
        var apiUrl = $"https://api.github.com/repos/{github.Repo}/releases/latest";
        logger.LogInformation("Resolving latest release of {Repo}", github.Repo);

        using var response = await http.GetAsync(apiUrl, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        foreach (var asset in json.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString()!;
            if (!Glob.IsMatch(name, github.AssetPattern))
                continue;

            var url = asset.GetProperty("browser_download_url").GetString()!;
            var tag = json.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            return new ResolvedArtifact(url, name, null, $"github:{github.Repo}@{tag ?? "latest"}");
        }

        throw new InvalidOperationException(
            $"No asset in the latest release of {github.Repo} matches pattern '{github.AssetPattern}'.");
    }

    /// <summary>Fetches a SHA256SUMS-style file and extracts the hash for the given file name.</summary>
    private async Task<string?> FetchChecksumAsync(string checksumUrl, string fileName, CancellationToken ct)
    {
        try
        {
            var body = await http.GetStringAsync(checksumUrl, ct);
            return ChecksumFile.FindSha256(body, fileName);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Checksum fetch from {Url} failed; continuing without one", checksumUrl);
            return null;
        }
    }

    public static string FileNameFromUrl(string url) => Path.GetFileName(new Uri(url).LocalPath);
}

/// <summary>Parses "sha256sum" output format: "&lt;hex&gt; &lt;filename&gt;" or "&lt;hex&gt; *&lt;filename&gt;" per line.</summary>
public static class ChecksumFile
{
    public static string? FindSha256(string content, string fileName)
    {
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var space = line.IndexOfAny([' ', '\t']);
            if (space < 0)
                continue;

            var hash = line[..space].Trim();
            var name = line[space..].Trim().TrimStart('*');
            if (hash.Length == 64 && string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                return hash.ToLowerInvariant();
        }

        return null;
    }
}

public static partial class Glob
{
    public static bool IsMatch(string text, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase);
    }
}

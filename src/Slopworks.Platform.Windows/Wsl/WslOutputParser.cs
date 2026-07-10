using System.Text.RegularExpressions;

namespace Slopworks.Platform.Windows.Wsl;

/// <summary>
/// Pure parsers for wsl.exe output, kept free of process plumbing so they can be tested
/// against captured fixtures. Parsing is tolerant of localization: it keys on version-number
/// shapes and line positions rather than exact English labels where possible.
/// </summary>
public static partial class WslOutputParser
{
    [GeneratedRegex(@"(\d+\.\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex VersionNumber();

    [GeneratedRegex(@":\s*([12])\s*$")]
    private static partial Regex TrailingVersionDigit();

    /// <summary>
    /// Parses "wsl --version" output. The first line carries the WSL version; the kernel is
    /// the line labeled kernel (English) or, failing that, the second versioned line.
    /// </summary>
    public static (string? WslVersion, string? KernelVersion) ParseVersionOutput(string stdout)
    {
        var lines = SplitLines(stdout);
        string? wsl = null, kernel = null;
        var versioned = new List<string>();

        foreach (var line in lines)
        {
            var match = VersionNumber().Match(line);
            if (!match.Success)
                continue;

            versioned.Add(match.Groups[1].Value);
            if (line.Contains("kernel", StringComparison.OrdinalIgnoreCase))
                kernel = match.Groups[1].Value;
        }

        if (versioned.Count > 0)
            wsl = versioned[0];
        kernel ??= versioned.Count > 1 ? versioned[1] : null;

        return (wsl, kernel);
    }

    /// <summary>Extracts "Default Version: 1|2" from "wsl --status" output.</summary>
    public static int? ParseDefaultVersion(string statusOutput)
    {
        foreach (var line in SplitLines(statusOutput))
        {
            var match = TrailingVersionDigit().Match(line);
            if (match.Success && line.Contains("version", StringComparison.OrdinalIgnoreCase))
                return int.Parse(match.Groups[1].Value);
        }

        return null;
    }

    public static bool IndicatesVirtualizationProblem(string output) =>
        output.Contains("Virtual Machine Platform", StringComparison.OrdinalIgnoreCase)
        || output.Contains("virtualization", StringComparison.OrdinalIgnoreCase)
        || output.Contains("BIOS", StringComparison.OrdinalIgnoreCase)
        || output.Contains("HCS_E_HYPERV_NOT_INSTALLED", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses "wsl --list --quiet": one distro name per line.</summary>
    public static IReadOnlyList<string> ParseDistroList(string stdout)
        => SplitLines(stdout);

    private static List<string> SplitLines(string text) =>
        [.. text
            .Replace("\0", "") // defense against mis-decoded UTF-16
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}

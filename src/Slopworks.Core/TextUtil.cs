using System.Text.RegularExpressions;

namespace Slopworks.Core;

public static partial class TextUtil
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRuns();

    /// <summary>
    /// Collapses command output into a single short line suitable for status text and step
    /// summaries. Full output belongs in the streaming pane / logs, never in the status bar.
    /// </summary>
    public static string Condense(string text, int maxLength = 300)
    {
        var condensed = WhitespaceRuns().Replace(text, " ").Trim();
        return condensed.Length <= maxLength ? condensed : condensed[..maxLength] + "…";
    }
}

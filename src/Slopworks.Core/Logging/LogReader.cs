using System.Text;
using Slopworks.Core.Config;

namespace Slopworks.Core.Logging;

public sealed record LogFileInfo(string Name, string FullPath, long SizeBytes, DateTimeOffset Modified);

/// <summary>
/// Reads Slopworks' own log files for the Logs view. Lists everything under logs/ (app,
/// command audit, vLLM server logs) newest first, and tails large files so a multi-MB log
/// never stalls the UI. Reads share the file with the live logger.
/// </summary>
public static class LogReader
{
    public const long MaxTailBytes = 512 * 1024;

    /// <summary>All log files under the logs directory (recursive), newest first.</summary>
    public static IReadOnlyList<LogFileInfo> ListLogFiles(SlopworksPaths paths)
    {
        if (!Directory.Exists(paths.LogsDir))
            return [];

        var logs = new List<LogFileInfo>();
        foreach (var path in Directory.EnumerateFiles(paths.LogsDir, "*.log", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            var name = Path.GetRelativePath(paths.LogsDir, path).Replace('\\', '/');
            logs.Add(new LogFileInfo(name, path, info.Length, info.LastWriteTime));
        }

        return [.. logs.OrderByDescending(l => l.Modified)];
    }

    /// <summary>
    /// Returns the tail of the file (up to MaxTailBytes), prefixed with a truncation notice
    /// when the file is larger. Opens shared so the running logger is never blocked.
    /// </summary>
    public static string ReadTail(string path, long maxBytes = MaxTailBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var truncated = stream.Length > maxBytes;
            if (truncated)
                stream.Seek(-maxBytes, SeekOrigin.End);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var body = reader.ReadToEnd();

            // Drop a partial first line after seeking mid-file.
            if (truncated)
            {
                var firstBreak = body.IndexOf('\n');
                if (firstBreak >= 0)
                    body = body[(firstBreak + 1)..];
                body = $"… showing the last {maxBytes / 1024} KB of {new FileInfo(path).Length / 1024} KB …\n\n" + body;
            }

            return body;
        }
        catch (IOException ex)
        {
            return $"(could not read {Path.GetFileName(path)}: {ex.Message})";
        }
    }
}

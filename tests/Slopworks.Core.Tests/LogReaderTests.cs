using System.Text;
using Slopworks.Core.Config;
using Slopworks.Core.Logging;
using Xunit;

namespace Slopworks.Core.Tests;

public class LogReaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("slopworks-logs-").FullName;

    private SlopworksPaths Paths => new(_root);

    [Fact]
    public void ListLogFiles_ReturnsEmptyWhenNoLogsDir()
        => Assert.Empty(LogReader.ListLogFiles(Paths));

    [Fact]
    public void ListLogFiles_FindsAppCommandAndVllmLogs_NewestFirst()
    {
        var p = Paths;
        Directory.CreateDirectory(p.VllmLogsDir);
        var older = Path.Combine(p.LogsDir, "app-20260101.log");
        var newer = Path.Combine(p.LogsDir, "commands-20260102.log");
        var nested = Path.Combine(p.VllmLogsDir, "server-abc.log");
        File.WriteAllText(older, "old");
        File.WriteAllText(newer, "new");
        File.WriteAllText(nested, "vllm");
        File.SetLastWriteTime(older, new DateTime(2026, 1, 1));
        File.SetLastWriteTime(newer, new DateTime(2026, 1, 2));
        File.SetLastWriteTime(nested, new DateTime(2026, 1, 3));

        var files = LogReader.ListLogFiles(p);

        Assert.Equal(3, files.Count);
        Assert.Equal("vllm/server-abc.log", files[0].Name); // newest first, forward slashes
        Assert.Equal("commands-20260102.log", files[1].Name);
        Assert.Equal("app-20260101.log", files[2].Name);
    }

    [Fact]
    public void ReadTail_ReturnsWholeSmallFile()
    {
        var file = Path.Combine(_root, "small.log");
        File.WriteAllText(file, "line one\nline two\n");

        Assert.Equal("line one\nline two\n", LogReader.ReadTail(file));
    }

    [Fact]
    public void ReadTail_TruncatesLargeFile_WithNoticeAndCleanFirstLine()
    {
        var file = Path.Combine(_root, "big.log");
        var sb = new StringBuilder();
        for (var i = 0; i < 100_000; i++)
            sb.Append($"line {i} with some padding text to add bytes\n");
        File.WriteAllText(file, sb.ToString());

        var tail = LogReader.ReadTail(file, maxBytes: 4096);

        Assert.Contains("showing the last 4 KB", tail);
        Assert.Contains("line 99999", tail);           // the end is present
        Assert.DoesNotContain("line 0 with", tail);     // the start is dropped
    }

    [Fact]
    public void ReadTail_WorksWhileFileIsOpenForWriting()
    {
        var file = Path.Combine(_root, "live.log");
        using var writer = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        writer.Write("in progress\n"u8);
        writer.Flush();

        var tail = LogReader.ReadTail(file);

        Assert.Contains("in progress", tail);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

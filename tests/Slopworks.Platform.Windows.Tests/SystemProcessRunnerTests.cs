using Slopworks.Platform.Abstractions;
using Slopworks.Platform.Windows;
using Xunit;

namespace Slopworks.Platform.Windows.Tests;

public class SystemProcessRunnerTests
{
    private readonly SystemProcessRunner _runner = new();

    [Fact]
    public async Task RunsProcess_CapturesStdoutAndExitCode()
    {
        var lines = new List<string>();
        var result = await _runner.RunAsync(
            new ProcessSpec("cmd.exe", ["/c", "echo hello slopworks"]),
            new SyncProgress(lines.Add),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Succeeded);
        Assert.Contains("hello slopworks", result.Stdout);
        Assert.Contains(lines, l => l.Contains("hello slopworks"));
    }

    [Fact]
    public async Task NonZeroExit_IsReportedNotThrown()
    {
        var result = await _runner.RunAsync(
            new ProcessSpec("cmd.exe", ["/c", "exit 3"]), null, CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Cancellation_KillsProcess()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _runner.RunAsync(
            new ProcessSpec("cmd.exe", ["/c", "ping -n 30 127.0.0.1 > nul"]), null, cts.Token));
    }

    [Fact]
    public async Task ElevationRequest_IsRejected()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => _runner.RunAsync(
            new ProcessSpec("cmd.exe", ["/c", "echo hi"], RequiresElevation: true), null, CancellationToken.None));
    }

    private sealed class SyncProgress(Action<string> handler) : IProgress<string>
    {
        public void Report(string value) => handler(value);
    }
}

using Slopworks.Core.Config;
using Slopworks.Core.State;
using Xunit;

namespace Slopworks.Core.Tests;

public class FileStateJournalTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("slopworks-journal-").FullName;

    private string JournalPath => Path.Combine(_dir, "journal.json");

    [Fact]
    public async Task SaveAndLoad_RoundTrips()
    {
        var journal = FileStateJournal.Load(JournalPath);
        journal.Data.Mode = "auto";
        journal.Data.PendingReboot = new PendingReboot { AfterStep = "wsl.feature", RequestedAt = DateTimeOffset.UtcNow };
        journal.Data.Steps["a"] = new StepJournalEntry { LastState = "Ok", LastVerifiedAt = DateTimeOffset.UtcNow };
        await journal.SaveAsync();

        var reloaded = FileStateJournal.Load(JournalPath);

        Assert.Equal("auto", reloaded.Data.Mode);
        Assert.Equal("wsl.feature", reloaded.Data.PendingReboot!.AfterStep);
        Assert.Equal("Ok", reloaded.Data.Steps["a"].LastState);
    }

    [Fact]
    public void CorruptJournal_StartsFresh()
    {
        File.WriteAllText(JournalPath, "{ not json !!!");

        var journal = FileStateJournal.Load(JournalPath);

        Assert.Null(journal.Data.PendingReboot);
        Assert.Empty(journal.Data.Steps);
    }

    [Fact]
    public void MissingJournal_StartsFresh()
    {
        var journal = FileStateJournal.Load(JournalPath);

        Assert.Equal(1, journal.Data.SchemaVersion);
        Assert.Empty(journal.Data.Steps);
    }

    [Fact]
    public async Task Save_LeavesNoTempFileBehind()
    {
        var journal = FileStateJournal.Load(JournalPath);
        await journal.SaveAsync();

        Assert.True(File.Exists(JournalPath));
        Assert.False(File.Exists(JournalPath + ".tmp"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}

public class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("slopworks-config-").FullName;

    [Fact]
    public void FirstRun_CreatesDefaultConfigFile()
    {
        var paths = new SlopworksPaths(_dir);

        var config = ConfigStore.LoadOrCreate(paths);

        Assert.True(File.Exists(paths.ConfigFile));
        Assert.Equal("safe", config.Mode);
        Assert.False(config.IsAutoMode);
        Assert.Contains("rootfs", config.Artifacts.Keys);
    }

    [Fact]
    public void SaveAndReload_PreservesEdits()
    {
        var paths = new SlopworksPaths(_dir);
        var config = ConfigStore.LoadOrCreate(paths);
        config.Mode = "auto";
        config.Server.Port = 9000;
        ConfigStore.Save(paths, config);

        var reloaded = ConfigStore.LoadOrCreate(paths);

        Assert.True(reloaded.IsAutoMode);
        Assert.Equal(9000, reloaded.Server.Port);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}

public class SlopworksPathsTests
{
    [Fact]
    public void Contains_IsTrueOnlyForPathsUnderRoot()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\Slopworks" : "/opt/slopworks";
        var paths = new SlopworksPaths(root);

        Assert.True(paths.Contains(Path.Combine(root, "wsl", "slopworks", "ext4.vhdx")));
        Assert.True(paths.Contains(root));
        Assert.False(paths.Contains(OperatingSystem.IsWindows() ? @"C:\Windows\System32" : "/usr/bin"));
        Assert.False(paths.Contains(root + "EvilTwin" + Path.DirectorySeparatorChar + "file"));
    }
}

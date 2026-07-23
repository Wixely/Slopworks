using Slopworks.Core.Config;
using Xunit;

namespace Slopworks.Core.Tests;

public class UiPrefsStoreTests
{
    [Fact]
    public void Load_WhenMissing_DefaultsLiveLogsOn()
    {
        var dir = Directory.CreateTempSubdirectory("slopworks-ui-").FullName;
        try
        {
            Assert.True(UiPrefsStore.Load(new SlopworksPaths(dir)).LiveLogs);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SaveThenLoad_RemembersTheChoice()
    {
        var dir = Directory.CreateTempSubdirectory("slopworks-ui-").FullName;
        try
        {
            var paths = new SlopworksPaths(dir);
            UiPrefsStore.Save(paths, new UiPrefs { LiveLogs = false });
            Assert.False(UiPrefsStore.Load(paths).LiveLogs);
        }
        finally { Directory.Delete(dir, true); }
    }
}

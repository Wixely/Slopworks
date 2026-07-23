using Slopworks.Core.Config;
using Xunit;

namespace Slopworks.Core.Tests;

public class ProfileStoreTests
{
    private static (ProfileStore store, string dir) NewStore()
    {
        var dir = Directory.CreateTempSubdirectory("slopworks-profiles-").FullName;
        return (new ProfileStore(new SlopworksPaths(dir)), dir);
    }

    [Fact]
    public void Create_ThenList_ThenLoad_RoundTripsTheConfig()
    {
        var (store, dir) = NewStore();
        try
        {
            var config = new SlopworksConfig();
            config.Server.Model = "org/my-model";
            config.Server.MaxModelLen = 16384;

            store.Create("Fast 5090", config);

            Assert.Contains("Fast 5090", store.List());
            var loaded = store.Load("Fast 5090");
            Assert.Equal("org/my-model", loaded.Server.Model);
            Assert.Equal(16384, loaded.Server.MaxModelLen);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Create_DuplicateName_Throws()
    {
        var (store, dir) = NewStore();
        try
        {
            store.Create("a", new SlopworksConfig());
            Assert.Throws<InvalidOperationException>(() => store.Create("a", new SlopworksConfig()));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Duplicate_CopiesContentUnderTheNewName()
    {
        var (store, dir) = NewStore();
        try
        {
            var config = new SlopworksConfig();
            config.Server.KvCacheDtype = "fp8";
            store.Create("src", config);

            store.Duplicate("src", "copy");

            Assert.Equal("fp8", store.Load("copy").Server.KvCacheDtype);
            Assert.Contains("copy", store.List());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Rename_MovesFile_KeepsContent_AndUpdatesActivePointer()
    {
        var (store, dir) = NewStore();
        try
        {
            var config = new SlopworksConfig();
            config.Server.Model = "org/keep-me";
            store.Create("old", config);
            store.SetActive("old");

            store.Rename("old", "New Name");

            Assert.DoesNotContain("old", store.List());
            Assert.Contains("New Name", store.List());
            Assert.Equal("New Name", store.Active);
            Assert.Equal("org/keep-me", store.Load("New Name").Server.Model);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Rename_ToExistingName_Throws()
    {
        var (store, dir) = NewStore();
        try
        {
            store.Create("a", new SlopworksConfig());
            store.Create("b", new SlopworksConfig());
            Assert.Throws<InvalidOperationException>(() => store.Rename("a", "b"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Delete_RemovesTheProfile()
    {
        var (store, dir) = NewStore();
        try
        {
            store.Create("gone", new SlopworksConfig());
            store.Delete("gone");
            Assert.DoesNotContain("gone", store.List());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Active_TracksThePointer_AndFallsBackWhenMissing()
    {
        var (store, dir) = NewStore();
        try
        {
            store.Create("one", new SlopworksConfig());
            store.Create("two", new SlopworksConfig());

            store.SetActive("two");
            Assert.Equal("two", store.Active);

            // Pointer at a deleted profile falls back to an existing one.
            store.Delete("two");
            Assert.Equal("one", store.Active);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EnsureInitialized_SnapshotsCurrentConfigAsDefault()
    {
        var (store, dir) = NewStore();
        try
        {
            var current = new SlopworksConfig();
            current.Server.Model = "org/from-legacy-config";

            store.EnsureInitialized(current);

            Assert.Equal([ProfileStore.DefaultName], store.List());
            Assert.Equal(ProfileStore.DefaultName, store.Active);
            Assert.Equal("org/from-legacy-config", store.Load(ProfileStore.DefaultName).Server.Model);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EnsureInitialized_LeavesExistingProfilesAlone()
    {
        var (store, dir) = NewStore();
        try
        {
            store.Create("mine", new SlopworksConfig());
            store.EnsureInitialized(new SlopworksConfig());

            Assert.DoesNotContain(ProfileStore.DefaultName, store.List());
            Assert.Equal("mine", store.Active);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("My Profile", "My Profile")]
    [InlineData("bad/name:here", "badnamehere")]
    [InlineData("v1.2", "v1.2")]            // inner dots are kept
    [InlineData(".hidden.", "hidden")]      // leading/trailing dots trimmed
    [InlineData("   ", "")]
    public void Clean_StripsUnsafeCharacters(string input, string expected)
        => Assert.Equal(expected, ProfileStore.Clean(input));

    [Fact]
    public void ConfigStore_Clone_IsDeepAndIndependent()
    {
        var original = new SlopworksConfig { Platform = "cuda" };
        original.Server.Model = "org/m";
        original.Images.Gpu = "img:1";

        var clone = ConfigStore.Clone(original);
        clone.Server.Model = "changed";
        clone.Images.Gpu = "changed";

        Assert.Equal("org/m", original.Server.Model); // original untouched by clone edits
        Assert.Equal("img:1", original.Images.Gpu);
        Assert.Equal("cuda", clone.Platform);         // fields carried across
    }

    [Fact]
    public void ConfigCopyFrom_OverwritesAllSections()
    {
        var target = new SlopworksConfig();
        var source = new SlopworksConfig { Mode = "auto" };
        source.Server.Model = "org/replacement";
        source.Images.Gpu = "custom/image:tag";

        target.CopyFrom(source);

        Assert.Equal("auto", target.Mode);
        Assert.Equal("org/replacement", target.Server.Model);
        Assert.Equal("custom/image:tag", target.Images.Gpu);
    }
}

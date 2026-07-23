using Slopworks.Core.Config;
using Xunit;

namespace Slopworks.Core.Tests;

public class PlatformStoreTests
{
    private static (PlatformStore store, string dir) NewStore()
    {
        var dir = Directory.CreateTempSubdirectory("slopworks-platforms-").FullName;
        return (new PlatformStore(new SlopworksPaths(dir)), dir);
    }

    private static PlatformProfile Sample(string gpuImage)
    {
        var p = new PlatformProfile();
        p.Images.Gpu = gpuImage;
        p.Distro.OnlineName = "Ubuntu-24.04";
        return p;
    }

    [Fact]
    public void Create_List_Load_RoundTrips()
    {
        var (store, dir) = NewStore();
        try
        {
            store.Create("cuda-nightly", Sample("docker.io/vllm/vllm-openai:nightly"));

            Assert.Contains("cuda-nightly", store.List());
            Assert.Equal("docker.io/vllm/vllm-openai:nightly", store.Load("cuda-nightly").Images.Gpu);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EnsureInitialized_SeedsDefaultFromCurrentConfig()
    {
        var (store, dir) = NewStore();
        try
        {
            store.EnsureInitialized(Sample("my/image:tag"));

            Assert.Equal([PlatformStore.DefaultName], store.List());
            Assert.Equal(PlatformStore.DefaultName, store.Default);
            Assert.Equal("my/image:tag", store.Load(PlatformStore.DefaultName).Images.Gpu);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SetDefault_TracksThePointer()
    {
        var (store, dir) = NewStore();
        try
        {
            store.Create("a", Sample("a/i"));
            store.Create("b", Sample("b/i"));

            store.SetDefault("b");
            Assert.Equal("b", store.Default);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Rename_UpdatesDefaultPointer()
    {
        var (store, dir) = NewStore();
        try
        {
            store.Create("old", Sample("old/i"));
            store.SetDefault("old");

            store.Rename("old", "new");

            Assert.Equal("new", store.Default);
            Assert.Equal("old/i", store.Load("new").Images.Gpu);
            Assert.DoesNotContain("old", store.List());
        }
        finally { Directory.Delete(dir, true); }
    }
}

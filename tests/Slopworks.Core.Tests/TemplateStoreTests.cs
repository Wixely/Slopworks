using Slopworks.Core.Config;
using Xunit;

namespace Slopworks.Core.Tests;

public class TemplateStoreTests
{
    private static (TemplateStore store, string dir) NewStore()
    {
        var dir = Directory.CreateTempSubdirectory("slopworks-templates-").FullName;
        return (new TemplateStore(new SlopworksPaths(dir)), dir);
    }

    [Fact]
    public void Create_ThenList_ThenLoad_RoundTripsTheContent()
    {
        var (store, dir) = NewStore();
        try
        {
            store.Create("Qwen 3.6 fixed", "{{ messages }}");

            Assert.Contains("Qwen 3.6 fixed", store.List());
            Assert.Equal("{{ messages }}", store.Load("Qwen 3.6 fixed"));
            // Stored as a .jinja file in the templates/ subdir, named from the cleaned display name.
            Assert.True(File.Exists(Path.Combine(dir, "templates", "Qwen 3.6 fixed.jinja")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Create_DuplicateName_Throws()
    {
        var (store, dir) = NewStore();
        try
        {
            store.Create("a", "x");
            Assert.Throws<InvalidOperationException>(() => store.Create("a", "y"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Duplicate_CopiesContentUnderTheNewName()
    {
        var (store, dir) = NewStore();
        try
        {
            store.Create("src", "template-body");
            store.Duplicate("src", "copy");

            Assert.Equal("template-body", store.Load("copy"));
            Assert.Contains("copy", store.List());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Rename_MovesFile_KeepsContent()
    {
        var (store, dir) = NewStore();
        try
        {
            store.Create("old", "keep-me");
            store.Rename("old", "New Name");

            Assert.DoesNotContain("old", store.List());
            Assert.Contains("New Name", store.List());
            Assert.Equal("keep-me", store.Load("New Name"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Rename_ToExistingName_Throws()
    {
        var (store, dir) = NewStore();
        try
        {
            store.Create("a", "1");
            store.Create("b", "2");
            Assert.Throws<InvalidOperationException>(() => store.Rename("a", "b"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Delete_RemovesTheTemplate()
    {
        var (store, dir) = NewStore();
        try
        {
            store.Create("gone", "x");
            store.Delete("gone");
            Assert.DoesNotContain("gone", store.List());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Save_OverwritesExistingContent()
    {
        var (store, dir) = NewStore();
        try
        {
            store.Create("t", "v1");
            store.Save("t", "v2");
            Assert.Equal("v2", store.Load("t"));
        }
        finally { Directory.Delete(dir, true); }
    }
}

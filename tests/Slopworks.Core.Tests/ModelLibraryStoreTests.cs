using Slopworks.Core.Config;
using Xunit;

namespace Slopworks.Core.Tests;

public class ModelLibraryStoreTests
{
    private static (ModelLibraryStore store, string dir) NewStore()
    {
        var dir = Directory.CreateTempSubdirectory("slopworks-models-").FullName;
        return (new ModelLibraryStore(new SlopworksPaths(dir)), dir);
    }

    [Fact]
    public void Load_WhenMissing_ReturnsEmptyDoc()
    {
        var (store, dir) = NewStore();
        try
        {
            var doc = store.Load();
            Assert.Empty(doc.Models);
            Assert.False(doc.ShowAdvanced);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEntriesNotesMetadataAndToggle()
    {
        var (store, dir) = NewStore();
        try
        {
            var doc = new ModelLibraryDoc { ShowAdvanced = true, TokenWarningShown = true };
            doc.Models.Add(new ModelEntry
            {
                Id = "Qwen/Qwen2.5-32B-Instruct-AWQ",
                Notes = "good on 2x3090",
                Verdict = "Servable",
                Summary = "Servable — awq safetensors",
                Detail = "Safetensors checkpoint, quantization method 'awq'. vLLM supports this method.",
                Quant = "awq",
                Architecture = "Qwen2ForCausalLM",
                Parameters = 32_763_876_352,
                CheckedAt = "2026-07-23 14:05",
            });

            store.Save(doc);
            var loaded = store.Load();

            Assert.True(loaded.ShowAdvanced);
            Assert.True(loaded.TokenWarningShown);
            var entry = Assert.Single(loaded.Models);
            Assert.Equal("Qwen/Qwen2.5-32B-Instruct-AWQ", entry.Id);
            Assert.Equal("good on 2x3090", entry.Notes);
            Assert.Equal("awq", entry.Quant);
            Assert.Equal(32_763_876_352, entry.Parameters);
            Assert.Contains("vLLM supports", entry.Detail);
            Assert.Equal("2026-07-23 14:05", entry.CheckedAt);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_CorruptFile_FallsBackToEmpty()
    {
        var (store, dir) = NewStore();
        try
        {
            File.WriteAllText(store.FilePath, "{ not valid json");
            Assert.Empty(store.Load().Models);
        }
        finally { Directory.Delete(dir, true); }
    }
}

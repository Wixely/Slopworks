using System.Text.Json;
using System.Text.Json.Serialization;

namespace Slopworks.Core.Config;

/// <summary>A model the user has saved to their library, with notes and cached HF metadata.</summary>
public sealed class ModelEntry
{
    /// <summary>HuggingFace repo id (or any value vLLM's --model accepts).</summary>
    public string Id { get; set; } = "";

    /// <summary>Free-form user notes.</summary>
    public string Notes { get; set; } = "";

    // Cached from the last "Check on HF" so the library shows metadata without re-fetching.
    public string? Verdict { get; set; }        // Servable / Caution / Unservable / Unknown
    public string? Summary { get; set; }        // one-line verdict
    public string? Detail { get; set; }         // full explanation
    public string? Quant { get; set; }          // awq / gptq / gguf / mlx / none / …
    public string? Architecture { get; set; }
    public long? Parameters { get; set; }
    public int? MaxContext { get; set; }        // config max_position_embeddings
    public string? Dtype { get; set; }          // config torch_dtype
    public string? Pipeline { get; set; }       // HF task, e.g. text-generation
    public string? License { get; set; }
    public bool Gated { get; set; }
    public long? Downloads { get; set; }

    /// <summary>When the metadata was last fetched, for display. Null if never checked.</summary>
    public string? CheckedAt { get; set; }
}

/// <summary>The persisted model library plus the Models-tab UI preference.</summary>
public sealed class ModelLibraryDoc
{
    public List<ModelEntry> Models { get; set; } = [];

    /// <summary>Whether the advanced (HF investigation) features are shown. Remembered across sessions.</summary>
    public bool ShowAdvanced { get; set; }

    /// <summary>Set once the no-token quota warning has been shown, so it never pops up again.</summary>
    public bool TokenWarningShown { get; set; }
}

/// <summary>
/// Persists the model library to <c>{root}/models.json</c>. Global (not per-profile) — your saved
/// models and notes are the same whichever settings profile is active.
/// </summary>
public sealed class ModelLibraryStore(SlopworksPaths paths)
{
    public string FilePath => Path.Combine(paths.Root, "models.json");

    public ModelLibraryDoc Load()
    {
        try
        {
            if (File.Exists(FilePath)
                && JsonSerializer.Deserialize(File.ReadAllText(FilePath), ModelLibraryJsonContext.Default.ModelLibraryDoc) is { } doc)
                return doc;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt library falls back to empty rather than blocking the app.
        }
        return new ModelLibraryDoc();
    }

    public void Save(ModelLibraryDoc doc)
    {
        Directory.CreateDirectory(paths.Root);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, ModelLibraryJsonContext.Default.ModelLibraryDoc));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ModelLibraryDoc))]
internal sealed partial class ModelLibraryJsonContext : JsonSerializerContext;

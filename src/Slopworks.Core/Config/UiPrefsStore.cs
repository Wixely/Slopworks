using System.Text.Json;
using System.Text.Json.Serialization;

namespace Slopworks.Core.Config;

/// <summary>Small app-wide UI preferences (not per-profile), remembered across sessions.</summary>
public sealed class UiPrefs
{
    /// <summary>Whether the Server tab's live log tail starts automatically. On by default.</summary>
    public bool LiveLogs { get; set; } = true;
}

/// <summary>
/// Persists <see cref="UiPrefs"/> to a tiny JSON file in the state folder. Corrupt or missing
/// files fall back to defaults (so a fresh install gets live logs on).
/// </summary>
public static class UiPrefsStore
{
    public static string FilePath(SlopworksPaths paths) => Path.Combine(paths.StateDir, "ui.json");

    public static UiPrefs Load(SlopworksPaths paths)
    {
        try
        {
            var file = FilePath(paths);
            if (File.Exists(file)
                && JsonSerializer.Deserialize(File.ReadAllText(file), UiPrefsJsonContext.Default.UiPrefs) is { } prefs)
                return prefs;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
        }
        return new UiPrefs();
    }

    public static void Save(SlopworksPaths paths, UiPrefs prefs)
    {
        try
        {
            Directory.CreateDirectory(paths.StateDir);
            var tmp = FilePath(paths) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(prefs, UiPrefsJsonContext.Default.UiPrefs));
            File.Move(tmp, FilePath(paths), overwrite: true);
        }
        catch (IOException)
        {
            // A lost UI-pref save is never worth surfacing an error for.
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UiPrefs))]
internal sealed partial class UiPrefsJsonContext : JsonSerializerContext;

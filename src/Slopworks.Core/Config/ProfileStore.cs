using System.Text.Json;
using Slopworks.Core.Serialization;

namespace Slopworks.Core.Config;

/// <summary>
/// Named settings files ("profiles"). Each is a full <see cref="SlopworksConfig"/> saved under
/// <see cref="SlopworksPaths.ProfilesDir"/>; a small pointer file records which one is active.
/// Pure file management — the App's ProfileManager coordinates switching the live config and UI.
/// </summary>
public sealed class ProfileStore(SlopworksPaths paths)
{
    public const string DefaultName = "default";

    public IReadOnlyList<string> List() =>
        Directory.Exists(paths.ProfilesDir)
            ? [.. Directory.EnumerateFiles(paths.ProfilesDir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)]
            : [];

    public bool Exists(string name) => File.Exists(PathFor(name));

    /// <summary>The active profile name — the pointer file, falling back to the first existing profile.</summary>
    public string Active
    {
        get
        {
            if (File.Exists(paths.ActiveProfileFile))
            {
                var name = File.ReadAllText(paths.ActiveProfileFile).Trim();
                if (name.Length > 0 && Exists(name))
                    return name;
            }
            return List().FirstOrDefault() ?? DefaultName;
        }
    }

    public void SetActive(string name)
    {
        Directory.CreateDirectory(paths.ProfilesDir);
        File.WriteAllText(paths.ActiveProfileFile, Clean(name));
    }

    public SlopworksConfig Load(string name)
    {
        var file = PathFor(name);
        if (File.Exists(file)
            && JsonSerializer.Deserialize(File.ReadAllText(file), SlopworksJsonContext.Default.SlopworksConfig) is { } config)
            return config;
        return new SlopworksConfig();
    }

    public void Save(string name, SlopworksConfig config)
    {
        Directory.CreateDirectory(paths.ProfilesDir);
        var file = PathFor(name);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, SlopworksJsonContext.Default.SlopworksConfig));
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>Create a new profile; throws if the name is invalid or already exists. Returns the clean name.</summary>
    public string Create(string name, SlopworksConfig config)
    {
        var clean = Clean(name);
        if (clean.Length == 0)
            throw new ArgumentException("Profile name cannot be empty.");
        if (Exists(clean))
            throw new InvalidOperationException($"A profile named '{clean}' already exists.");
        Save(clean, config);
        return clean;
    }

    public string Duplicate(string source, string newName) => Create(newName, Load(source));

    public void Delete(string name)
    {
        var file = PathFor(name);
        if (File.Exists(file))
            File.Delete(file);
    }

    /// <summary>
    /// First-run setup: if there are no profiles yet, snapshot the current config as "default"
    /// (migrating an existing config.json), and make sure the active pointer names a real profile.
    /// </summary>
    public void EnsureInitialized(SlopworksConfig current)
    {
        Directory.CreateDirectory(paths.ProfilesDir);
        if (List().Count == 0)
        {
            Save(DefaultName, current);
            SetActive(DefaultName);
        }
        else if (!Exists(Active))
        {
            SetActive(List().First());
        }
    }

    private string PathFor(string name) => Path.Combine(paths.ProfilesDir, Clean(name) + ".json");

    /// <summary>Reduce a display name to a safe file base (drops path/invalid chars, trims, collapses spaces).</summary>
    public static string Clean(string name)
    {
        var trimmed = (name ?? "").Trim();
        var kept = new string([.. trimmed.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c != '.')]);
        return kept.Trim();
    }

    public static bool IsValidName(string name) => Clean(name).Length > 0;
}

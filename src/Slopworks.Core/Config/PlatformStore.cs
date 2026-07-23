using System.Text.Json;
using System.Text.Json.Serialization;

namespace Slopworks.Core.Config;

/// <summary>A named platform: the container images and distro source used to run a model.</summary>
public sealed class PlatformProfile
{
    public ImagesConfig Images { get; set; } = new();
    public DistroConfig Distro { get; set; } = new();

    /// <summary>The rootfs download source (used in tarball distro mode).</summary>
    public ArtifactSource Rootfs { get; set; } = new();
}

/// <summary>
/// Named platform files under <c>{root}/platforms/*.json</c>, with a pointer to the default one.
/// Global (not per-profile); a settings profile references a platform by name (empty = default).
/// </summary>
public sealed class PlatformStore(SlopworksPaths paths)
{
    public const string DefaultName = "default";

    public IReadOnlyList<string> List() =>
        Directory.Exists(paths.PlatformsDir)
            ? [.. Directory.EnumerateFiles(paths.PlatformsDir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)]
            : [];

    public bool Exists(string name) => File.Exists(PathFor(name));

    /// <summary>The default platform name — the pointer file, falling back to the first that exists.</summary>
    public string Default
    {
        get
        {
            if (File.Exists(paths.DefaultPlatformFile))
            {
                var name = File.ReadAllText(paths.DefaultPlatformFile).Trim();
                if (name.Length > 0 && Exists(name))
                    return name;
            }
            return List().FirstOrDefault() ?? DefaultName;
        }
    }

    public void SetDefault(string name)
    {
        Directory.CreateDirectory(paths.PlatformsDir);
        File.WriteAllText(paths.DefaultPlatformFile, ProfileStore.Clean(name));
    }

    public PlatformProfile Load(string name)
    {
        var file = PathFor(name);
        if (File.Exists(file)
            && JsonSerializer.Deserialize(File.ReadAllText(file), PlatformJsonContext.Default.PlatformProfile) is { } platform)
            return platform;
        return new PlatformProfile();
    }

    public void Save(string name, PlatformProfile platform)
    {
        Directory.CreateDirectory(paths.PlatformsDir);
        var file = PathFor(name);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(platform, PlatformJsonContext.Default.PlatformProfile));
        File.Move(tmp, file, overwrite: true);
    }

    public string Create(string name, PlatformProfile platform)
    {
        var clean = ProfileStore.Clean(name);
        if (clean.Length == 0)
            throw new ArgumentException("Platform name cannot be empty.");
        if (Exists(clean))
            throw new InvalidOperationException($"A platform named '{clean}' already exists.");
        Save(clean, platform);
        return clean;
    }

    public string Duplicate(string source, string newName) => Create(newName, Load(source));

    public string Rename(string oldName, string newName)
    {
        var clean = ProfileStore.Clean(newName);
        if (clean.Length == 0)
            throw new ArgumentException("Platform name cannot be empty.");
        if (!Exists(oldName))
            throw new InvalidOperationException($"No platform named '{oldName}'.");
        if (clean.Equals(oldName, StringComparison.Ordinal))
            return clean;

        var caseOnly = clean.Equals(oldName, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && Exists(clean))
            throw new InvalidOperationException($"A platform named '{clean}' already exists.");

        if (caseOnly)
        {
            var temp = Path.Combine(paths.PlatformsDir, clean + ".rename.tmp");
            File.Move(PathFor(oldName), temp);
            File.Move(temp, PathFor(clean));
        }
        else
        {
            File.Move(PathFor(oldName), PathFor(clean));
        }

        if (string.Equals(Default, oldName, StringComparison.OrdinalIgnoreCase))
            SetDefault(clean);
        return clean;
    }

    public void Delete(string name)
    {
        var file = PathFor(name);
        if (File.Exists(file))
            File.Delete(file);
    }

    /// <summary>First-run setup: seed a "default" platform (from the current config) if none exist.</summary>
    public void EnsureInitialized(PlatformProfile seed)
    {
        Directory.CreateDirectory(paths.PlatformsDir);
        if (List().Count == 0)
        {
            Save(DefaultName, seed);
            SetDefault(DefaultName);
        }
        else if (!Exists(Default))
        {
            SetDefault(List().First());
        }
    }

    private string PathFor(string name) => Path.Combine(paths.PlatformsDir, ProfileStore.Clean(name) + ".json");
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PlatformProfile))]
internal sealed partial class PlatformJsonContext : JsonSerializerContext;

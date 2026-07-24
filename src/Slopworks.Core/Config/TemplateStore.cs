namespace Slopworks.Core.Config;

/// <summary>
/// User chat-template files ("*.jinja") under <see cref="SlopworksPaths.TemplatesDir"/>. Files-only,
/// like <see cref="ProfileStore"/> — the file base name is the identity; the content is raw Jinja.
/// A model references one by name via <c>ModelEntry.ChatTemplate</c>; the server controller mounts
/// the served model's template into the container as --chat-template.
/// </summary>
public sealed class TemplateStore(SlopworksPaths paths)
{
    private const string Ext = ".jinja";

    public string Dir => paths.TemplatesDir;

    public IReadOnlyList<string> List() =>
        Directory.Exists(paths.TemplatesDir)
            ? [.. Directory.EnumerateFiles(paths.TemplatesDir, "*" + Ext)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)]
            : [];

    public bool Exists(string name) => File.Exists(PathFor(name));

    /// <summary>Absolute path of a template file (name cleaned to a safe file base).</summary>
    public string PathFor(string name) => Path.Combine(paths.TemplatesDir, ProfileStore.Clean(name) + Ext);

    public string Load(string name)
    {
        var file = PathFor(name);
        return File.Exists(file) ? File.ReadAllText(file) : "";
    }

    public void Save(string name, string content)
    {
        Directory.CreateDirectory(paths.TemplatesDir);
        var file = PathFor(name);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>Create a new template; throws if the name is invalid or already exists. Returns the clean name.</summary>
    public string Create(string name, string content)
    {
        var clean = ProfileStore.Clean(name);
        if (clean.Length == 0)
            throw new ArgumentException("Template name cannot be empty.");
        if (Exists(clean))
            throw new InvalidOperationException($"A template named '{clean}' already exists.");
        Save(clean, content);
        return clean;
    }

    public string Duplicate(string source, string newName) => Create(newName, Load(source));

    /// <summary>Rename a template file. Returns the clean new name.</summary>
    public string Rename(string oldName, string newName)
    {
        var clean = ProfileStore.Clean(newName);
        if (clean.Length == 0)
            throw new ArgumentException("Template name cannot be empty.");
        if (!Exists(oldName))
            throw new InvalidOperationException($"No template named '{oldName}'.");
        if (clean.Equals(oldName, StringComparison.Ordinal))
            return clean; // no change

        var caseOnly = clean.Equals(oldName, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && Exists(clean))
            throw new InvalidOperationException($"A template named '{clean}' already exists.");

        if (caseOnly)
        {
            // A case-only rename on a case-insensitive filesystem needs a two-step move.
            var temp = Path.Combine(paths.TemplatesDir, clean + ".rename.tmp");
            File.Move(PathFor(oldName), temp);
            File.Move(temp, PathFor(clean));
        }
        else
        {
            File.Move(PathFor(oldName), PathFor(clean));
        }

        return clean;
    }

    public void Delete(string name)
    {
        var file = PathFor(name);
        if (File.Exists(file))
            File.Delete(file);
    }
}

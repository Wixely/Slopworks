namespace Slopworks.Core.Config;

/// <summary>
/// Resolves the Slopworks root directory. A one-line pointer file in the per-user app-data
/// folder (the ONLY file Slopworks keeps outside its root) records the choice so the app
/// can be moved or reinstalled without losing its data.
/// </summary>
public static class RootLocator
{
    public static string PointerFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Slopworks", "rootpath.txt");

    public static string Resolve()
    {
        if (File.Exists(PointerFile))
        {
            var recorded = File.ReadAllText(PointerFile).Trim();
            if (recorded.Length > 0)
                return recorded;
        }

        var root = DefaultRoot();
        try
        {
            Directory.CreateDirectory(root);
        }
        catch (UnauthorizedAccessException)
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Slopworks");
            Directory.CreateDirectory(root);
        }

        Remember(root);
        return root;
    }

    public static void Remember(string root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PointerFile)!);
        File.WriteAllText(PointerFile, root);
    }

    private static string DefaultRoot() => OperatingSystem.IsWindows()
        ? Path.Combine(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? @"C:\", "Slopworks")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "slopworks");
}

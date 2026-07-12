using Slopworks.Core.Config;
using Xunit;

namespace Slopworks.Core.Tests;

public class WindowSizeStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("slopworks-window-").FullName;

    private SlopworksPaths Paths => new(_dir);

    [Fact]
    public void SaveAndLoad_RoundTripsSize()
    {
        WindowSizeStore.Save(Paths, 1280, 800);

        var loaded = WindowSizeStore.Load(Paths);

        Assert.NotNull(loaded);
        Assert.Equal(1280, loaded.Width);
        Assert.Equal(800, loaded.Height);
    }

    [Fact]
    public void Load_ReturnsNullWhenNothingSaved()
        => Assert.Null(WindowSizeStore.Load(Paths));

    [Fact]
    public void Save_IgnoresSillySmallSizes()
    {
        WindowSizeStore.Save(Paths, 50, 30);

        Assert.Null(WindowSizeStore.Load(Paths));
    }

    [Fact]
    public void Save_IgnoresNonFiniteSizes()
    {
        WindowSizeStore.Save(Paths, double.NaN, double.PositiveInfinity);

        Assert.Null(WindowSizeStore.Load(Paths));
    }

    [Fact]
    public void Load_ReturnsNullOnCorruptFile()
    {
        Directory.CreateDirectory(Paths.StateDir);
        File.WriteAllText(WindowSizeStore.FilePath(Paths), "{ not json");

        Assert.Null(WindowSizeStore.Load(Paths));
    }

    [Fact]
    public void Load_RejectsBelowMinimumSize()
    {
        Directory.CreateDirectory(Paths.StateDir);
        File.WriteAllText(WindowSizeStore.FilePath(Paths), """{"width":100,"height":50}""");

        Assert.Null(WindowSizeStore.Load(Paths));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}

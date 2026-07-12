using Slopworks.Platform.Linux;
using Xunit;

namespace Slopworks.Platform.Linux.Tests;

public class ProcStatParserTests
{
    private const string ProcStat = """
        cpu  74608 2520 24433 1117073 6176 4054 0 0 0 0
        cpu0 17977 551 6167 279059 1434 1191 0 0 0 0
        intr 33124101
        """;

    [Fact]
    public void ParsesAggregateLine_IdleIncludesIowait()
    {
        var (idle, total) = ProcStatParser.ParseCpuLine(ProcStat)!.Value;

        Assert.Equal(1117073ul + 6176ul, idle);
        Assert.Equal(74608ul + 2520 + 24433 + 1117073 + 6176 + 4054, total);
    }

    [Theory]
    [InlineData("")]
    [InlineData("cpu0 1 2 3 4")]
    [InlineData("cpu garbage")]
    public void MalformedInput_ReturnsNull(string input)
        => Assert.Null(ProcStatParser.ParseCpuLine(input));
}

public class MemInfoParserTests
{
    private const string MemInfo = """
        MemTotal:       32718940 kB
        MemFree:         1298348 kB
        MemAvailable:   24720312 kB
        Buffers:         1381996 kB
        """;

    [Fact]
    public void ParsesTotalAndAvailable_InBytes()
    {
        var (total, available) = MemInfoParser.Parse(MemInfo)!.Value;

        Assert.Equal(32718940L * 1024, total);
        Assert.Equal(24720312L * 1024, available);
    }

    [Fact]
    public void MissingFields_ReturnsNull()
        => Assert.Null(MemInfoParser.Parse("MemTotal: 100 kB"));
}

public class LspciParserTests
{
    [Fact]
    public void DetectsNvidiaVendorId_EvenWithNouveauOrNoDriver()
    {
        const string lspci = """
            00:02.0 VGA compatible controller [0300]: Intel Corporation UHD Graphics 630 [8086:9bc8]
            01:00.0 VGA compatible controller [0300]: NVIDIA Corporation AD102 [GeForce RTX 4090] [10de:2684] (rev a1)
            """;

        Assert.True(LspciParser.ContainsNvidiaDevice(lspci));
    }

    [Fact]
    public void IntelOnly_NotDetected()
    {
        const string lspci = "00:02.0 VGA compatible controller [0300]: Intel Corporation UHD Graphics 630 [8086:9bc8]";

        Assert.False(LspciParser.ContainsNvidiaDevice(lspci));
    }
}

public class OsReleaseParserTests
{
    private const string Ubuntu2404 = """
        PRETTY_NAME="Ubuntu 24.04.1 LTS"
        NAME="Ubuntu"
        VERSION_ID="24.04"
        ID=ubuntu
        ID_LIKE=debian
        """;

    [Fact]
    public void ReadsPrettyNameAndUbuntuVersion()
    {
        Assert.Equal("Ubuntu 24.04.1 LTS", OsReleaseParser.GetPrettyName(Ubuntu2404));
        Assert.Equal(new Version(24, 4), OsReleaseParser.GetUbuntuVersion(Ubuntu2404));
    }

    [Fact]
    public void NonUbuntu_HasNoUbuntuVersion()
    {
        const string fedora = """
            NAME="Fedora Linux"
            VERSION_ID=40
            ID=fedora
            """;

        Assert.Null(OsReleaseParser.GetUbuntuVersion(fedora));
    }
}

public class UfwParserTests
{
    [Theory]
    [InlineData("Status: active\n\nTo    Action    From", true)]
    [InlineData("Status: inactive", false)]
    [InlineData("", false)]
    public void DetectsActiveFirewall(string output, bool expected)
        => Assert.Equal(expected, UfwParser.IsActive(output));
}

public class HostLinuxCommandFactoryTests
{
    private readonly HostLinuxCommandFactory _factory = new();

    [Fact]
    public void RootScript_GoesThroughPkexec()
    {
        var spec = _factory.Script("apt-get install -y podman", user: "root");

        Assert.Equal("pkexec", spec.Exe);
        Assert.Equal(["bash", "-s"], spec.Args);
        Assert.Contains("apt-get", spec.StdinText);
    }

    [Fact]
    public void UserCommand_RunsDirectly()
    {
        var spec = _factory.Command("podman ps", user: "slop");

        Assert.Equal("bash", spec.Exe);
    }

    [Fact]
    public void Terminate_IsANoOp_NeverRestartsTheHost()
    {
        var spec = _factory.Terminate();

        Assert.Equal("true", spec.Exe);
        Assert.DoesNotContain("reboot", spec.CommandLineDisplay);
        Assert.DoesNotContain("shutdown", spec.CommandLineDisplay);
    }
}

public class LinuxShellIntegrationTests
{
    [Fact]
    public void DesktopEntry_IsValidAutostartFile()
    {
        var entry = LinuxShellIntegration.BuildDesktopEntry("/opt/slopworks/Slopworks.App");

        Assert.StartsWith("[Desktop Entry]", entry);
        Assert.Contains("Exec=\"/opt/slopworks/Slopworks.App\"", entry);
        Assert.Contains("Type=Application", entry);
    }
}

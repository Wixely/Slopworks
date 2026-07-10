using Slopworks.Platform.Windows.Wsl;
using Xunit;

namespace Slopworks.Platform.Windows.Tests;

public class WslOutputParserTests
{
    private const string ModernVersionOutput = """
        WSL version: 2.5.7.0
        Kernel version: 6.6.87.2-1
        WSLg version: 1.0.66
        MSRDC version: 1.2.6074
        Direct3D version: 1.611.1-81528511
        DXCore version: 10.0.26100.1-240331-1435.ge-release
        Windows version: 10.0.19045.5965
        """;

    private const string ModernStatusOutput = """
        Default Distribution: Ubuntu
        Default Version: 2
        """;

    private const string LegacyStatusOutput = """
        Default Version: 1

        The Windows Subsystem for Linux kernel can be manually updated with 'wsl --update', but automatic updates cannot occur due to your system settings.

        The WSL 2 kernel file is not found. To update or restore the kernel please run 'wsl --update'.
        """;

    private const string VirtualizationErrorOutput = """
        Please enable the Virtual Machine Platform Windows feature and ensure virtualization is enabled in the BIOS.
        For information please visit https://aka.ms/enablevirtualization
        """;

    [Fact]
    public void ParseVersionOutput_ExtractsWslAndKernelVersions()
    {
        var (wsl, kernel) = WslOutputParser.ParseVersionOutput(ModernVersionOutput);

        Assert.Equal("2.5.7.0", wsl);
        Assert.Equal("6.6.87.2", kernel);
    }

    [Fact]
    public void ParseVersionOutput_ToleratesEmptyOutput()
    {
        var (wsl, kernel) = WslOutputParser.ParseVersionOutput("");

        Assert.Null(wsl);
        Assert.Null(kernel);
    }

    [Fact]
    public void ParseVersionOutput_FallsBackToSecondVersionedLineForKernel()
    {
        // Localized output: labels unknown, positions still hold.
        var (wsl, kernel) = WslOutputParser.ParseVersionOutput("""
            Version WSL : 2.4.13.0
            Version du noyau : 5.15.167.4-1
            """);

        Assert.Equal("2.4.13.0", wsl);
        Assert.Equal("5.15.167.4", kernel);
    }

    [Theory]
    [InlineData(ModernStatusOutput, 2)]
    [InlineData(LegacyStatusOutput, 1)]
    [InlineData("no version line here", null)]
    public void ParseDefaultVersion_ReadsTrailingDigit(string output, int? expected)
    {
        Assert.Equal(expected, WslOutputParser.ParseDefaultVersion(output));
    }

    [Fact]
    public void IndicatesVirtualizationProblem_MatchesKnownErrorText()
    {
        Assert.True(WslOutputParser.IndicatesVirtualizationProblem(VirtualizationErrorOutput));
        Assert.False(WslOutputParser.IndicatesVirtualizationProblem(ModernStatusOutput));
    }

    [Fact]
    public void ParseDistroList_TrimsAndDropsEmptyLines()
    {
        var distros = WslOutputParser.ParseDistroList("Ubuntu\r\nslopworks\r\n\r\n");

        Assert.Equal(["Ubuntu", "slopworks"], distros);
    }

    [Fact]
    public void ParseDistroList_SurvivesMisdecodedUtf16NullBytes()
    {
        // If UTF-16LE output is ever decoded as UTF-8, every other byte is NUL.
        var distros = WslOutputParser.ParseDistroList("U\0b\0u\0n\0t\0u\0\r\0\n\0");

        Assert.Equal(["Ubuntu"], distros);
    }
}

public class NvidiaSmiParserTests
{
    [Fact]
    public void ParseQueryCsv_ReadsNameDriverAndMemory()
    {
        var gpu = NvidiaSmiParser.ParseQueryCsv("NVIDIA GeForce RTX 4090, 560.94, 24564\r\n");

        Assert.NotNull(gpu);
        Assert.Equal("NVIDIA GeForce RTX 4090", gpu.Name);
        Assert.Equal("560.94", gpu.DriverVersion);
        Assert.Equal(24564, gpu.MemoryMiB);
    }

    [Fact]
    public void ParseQueryCsv_UsesFirstGpuOnMultiGpuMachines()
    {
        var gpu = NvidiaSmiParser.ParseQueryCsv("""
            NVIDIA RTX A6000, 555.85, 49140
            NVIDIA RTX A6000, 555.85, 49140
            """);

        Assert.Equal(49140, gpu!.MemoryMiB);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage output")]
    [InlineData("name only, no memory")]
    public void ParseQueryCsv_ReturnsNullOnMalformedOutput(string output)
    {
        Assert.Null(NvidiaSmiParser.ParseQueryCsv(output));
    }
}

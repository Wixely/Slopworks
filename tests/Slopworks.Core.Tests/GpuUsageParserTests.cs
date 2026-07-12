using Slopworks.Core.Platform;
using Xunit;

namespace Slopworks.Core.Tests;

public class GpuUsageParserTests
{
    [Fact]
    public void ParsesMultiGpuOutput_OneEntryPerCard()
    {
        var gpus = GpuUsageParser.ParseCsv("""
            0, NVIDIA GeForce RTX 4090, 87, 20480, 24564
            1, NVIDIA RTX A6000, 3, 512, 49140
            """);

        Assert.Equal(2, gpus.Count);
        Assert.Equal("NVIDIA GeForce RTX 4090", gpus[0].Name);
        Assert.Equal(87, gpus[0].UtilizationPercent);
        Assert.Equal(1, gpus[1].Index);
        Assert.Equal(3, gpus[1].UtilizationPercent);
    }

    [Fact]
    public void VramPercent_ComputedFromUsedOverTotal()
    {
        var gpus = GpuUsageParser.ParseCsv("0, RTX, 50, 12282, 24564");

        Assert.Equal(50.0, gpus[0].VramPercent, precision: 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("0, name only, no numbers")]
    public void MalformedOutput_YieldsNoGpus(string output)
        => Assert.Empty(GpuUsageParser.ParseCsv(output));

    [Fact]
    public void UtilizationIsClamped()
    {
        var gpus = GpuUsageParser.ParseCsv("0, RTX, 250, 0, 24564");

        Assert.Equal(100, gpus[0].UtilizationPercent);
    }
}

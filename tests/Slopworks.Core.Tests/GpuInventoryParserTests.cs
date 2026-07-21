using Slopworks.Core.Platform;
using Xunit;

namespace Slopworks.Core.Tests;

public class GpuInventoryParserTests
{
    [Fact]
    public void Parse_WithPci_ReadsIndexNamePciAndMemory()
    {
        var gpus = GpuInventoryParser.Parse("""
            0, NVIDIA GeForce RTX 5090, 00000000:01:00.0, 32607
            1, NVIDIA GeForce RTX 3090, 00000000:21:00.0, 24576
            2, NVIDIA GeForce RTX 3090, 00000000:41:00.0, 24576
            """);

        Assert.Equal(3, gpus.Count);
        Assert.Equal("NVIDIA GeForce RTX 5090", gpus[0].Name);
        Assert.Equal("00000000:01:00.0", gpus[0].PciBusId);
        Assert.True(gpus[0].HasPci);
        Assert.Equal(32607, gpus[0].MemoryMiB);
        Assert.Equal(2, gpus[2].Index);
    }

    [Fact]
    public void Parse_WithoutPci_LeavesPciBlank()
    {
        var gpus = GpuInventoryParser.Parse("0, NVIDIA RTX A6000, 49140", hasPci: false);

        Assert.Single(gpus);
        Assert.Equal("NVIDIA RTX A6000", gpus[0].Name);
        Assert.False(gpus[0].HasPci);
        Assert.Equal(49140, gpus[0].MemoryMiB);
    }

    [Fact]
    public void Describe_IncludesIndexNamePciAndMemory()
    {
        var gpu = new GpuDevice(1, "NVIDIA GeForce RTX 3090", "00000000:21:00.0", 24576);

        var text = gpu.Describe();

        Assert.Contains("GPU 1", text);
        Assert.Contains("RTX 3090", text);
        Assert.Contains("24 GB", text);
        Assert.Contains("PCI 00000000:21:00.0", text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage line with no comma")]
    public void Parse_MalformedInput_YieldsNothing(string input)
        => Assert.Empty(GpuInventoryParser.Parse(input));
}

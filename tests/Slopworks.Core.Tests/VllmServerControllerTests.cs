using Slopworks.Core.Config;
using Slopworks.Core.Engine;
using Slopworks.Core.Platform;
using Slopworks.Core.Server;
using Xunit;

namespace Slopworks.Core.Tests;

public class VllmServerControllerTests
{
    private static VllmServerController Build(SlopworksConfig config)
        => new(new WslLinuxCommandFactory(SlopworksPaths.DistroName), config, new HttpClient());

    private static SystemProfile GpuProfile => new()
    {
        Gpu = new GpuInfo("NVIDIA RTX 4090", "560.94", 24564),
    };

    [Fact]
    public void GpuCommand_HasDeviceIpcAndMemoryUtilization()
    {
        var command = Build(new SlopworksConfig()).BuildRunCommand(GpuProfile, "org/model");

        Assert.Contains("--device nvidia.com/gpu=all", command);
        Assert.Contains("--ipc=host", command);
        Assert.Contains("--gpu-memory-utilization 0.9", command);
        Assert.Contains("--model org/model", command);
    }

    [Fact]
    public void CpuCommand_UsesCpuImageWithoutGpuFlags()
    {
        var config = new SlopworksConfig();
        var command = Build(config).BuildRunCommand(new SystemProfile(), "org/model");

        Assert.Contains(config.Images.Cpu, command);
        Assert.DoesNotContain("--device", command);
        Assert.Contains("VLLM_CPU_KVCACHE_SPACE", command);
    }

    [Fact]
    public void ExtraContainerArgs_LandBeforeTheImage_VllmArgsAfterTheModel()
    {
        var config = new SlopworksConfig();
        config.Server.ExtraContainerArgs = ["--memory 24g"];
        config.Server.ExtraArgs = ["--max-model-len 8192"];

        var command = Build(config).BuildRunCommand(GpuProfile, "org/model");

        var memoryIndex = command.IndexOf("--memory 24g", StringComparison.Ordinal);
        var imageIndex = command.IndexOf(config.Images.Gpu, StringComparison.Ordinal);
        var modelIndex = command.IndexOf("--model org/model", StringComparison.Ordinal);
        var vllmArgIndex = command.IndexOf("--max-model-len 8192", StringComparison.Ordinal);

        Assert.True(memoryIndex > 0 && memoryIndex < imageIndex, "container args must precede the image");
        Assert.True(vllmArgIndex > modelIndex, "vLLM args must follow the model");
    }

    [Fact]
    public void HfToken_NeverAppearsInTheCommandLine()
    {
        var config = new SlopworksConfig();
        config.Server.HfToken = "hf_supersecret";

        var command = Build(config).BuildRunCommand(GpuProfile, "org/model");

        Assert.DoesNotContain("hf_supersecret", command);
        Assert.Contains("HUGGING_FACE_HUB_TOKEN", command); // env-var indirection instead
    }
}

using Slopworks.Core.Config;
using Slopworks.Core.Engine;
using Slopworks.Core.Platform;
using Slopworks.Core.Server;
using Xunit;

namespace Slopworks.Core.Tests;

public class VllmServerControllerTests
{
    private static VllmServerController Build(SlopworksConfig config)
        => new(new WslLinuxCommandFactory(SlopworksPaths.DistroName), config, new HttpClient(),
            new SlopworksPaths(Path.Combine(Path.GetTempPath(), "slopworks-tests")));

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
    public void GpuCommand_EnablesWslPinMemory_OnWindowsOnly()
    {
        var command = Build(new SlopworksConfig()).BuildRunCommand(GpuProfile, "org/model");

        // On WSL (Windows) the V2 runner needs the pinned-memory opt-in; on a Linux host it doesn't.
        if (OperatingSystem.IsWindows())
            Assert.Contains("VLLM_WSL2_ENABLE_PIN_MEMORY=1", command);
        else
            Assert.DoesNotContain("VLLM_WSL2_ENABLE_PIN_MEMORY", command);
    }

    [Fact]
    public void TensorParallelAndVisibleGpus_ComposeIntoTheCommand()
    {
        var config = new SlopworksConfig();
        config.Server.TensorParallelSize = 2;
        config.Server.VisibleGpus = "1,2";
        config.Server.CudaDeviceOrder = "PCI_BUS_ID";

        var command = Build(config).BuildRunCommand(GpuProfile, "org/model");

        Assert.Contains("-e CUDA_VISIBLE_DEVICES=1,2", command);
        Assert.Contains("-e CUDA_DEVICE_ORDER=PCI_BUS_ID", command);
        Assert.Contains("--tensor-parallel-size 2", command);
    }

    [Fact]
    public void DeviceOrder_Unset_OmitsTheEnvVar()
    {
        var command = Build(new SlopworksConfig()).BuildRunCommand(GpuProfile, "org/model");

        Assert.DoesNotContain("CUDA_DEVICE_ORDER", command);
    }

    private static SystemProfile GpuProfileNvLink => GpuProfile with { HasNvLink = true };

    [Fact]
    public void MultiGpu_OnWindows_NoNvLink_DisablesBothIpcPaths()
    {
        var config = new SlopworksConfig();
        config.Server.TensorParallelSize = 2;

        var command = Build(config).BuildRunCommand(GpuProfile, "org/model"); // GpuProfile has no NVLink

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("--disable-custom-all-reduce", command);
            Assert.Contains("-e NCCL_P2P_DISABLE=1", command);
        }
    }

    [Fact]
    public void MultiGpu_OnWindows_WithNvLink_KeepsP2PButStillDisablesCustomAllReduce()
    {
        var config = new SlopworksConfig();
        config.Server.TensorParallelSize = 2;

        var command = Build(config).BuildRunCommand(GpuProfileNvLink, "org/model");

        if (OperatingSystem.IsWindows())
        {
            // Custom all-reduce (CUDA IPC) is always unavailable on WSL...
            Assert.Contains("--disable-custom-all-reduce", command);
            // ...but NVLink works, so NCCL P2P must NOT be disabled.
            Assert.DoesNotContain("NCCL_P2P_DISABLE", command);
        }
    }

    [Fact]
    public void MultiGpu_ExplicitDisableP2P_OverridesNvLink()
    {
        var config = new SlopworksConfig();
        config.Server.TensorParallelSize = 2;
        config.Server.DisableGpuP2P = true;

        var command = Build(config).BuildRunCommand(GpuProfileNvLink, "org/model");

        if (OperatingSystem.IsWindows())
            Assert.Contains("-e NCCL_P2P_DISABLE=1", command);
    }

    [Fact]
    public void SingleGpu_DoesNotDisableCustomAllReduce()
    {
        var command = Build(new SlopworksConfig()).BuildRunCommand(GpuProfile, "org/model");

        Assert.DoesNotContain("disable-custom-all-reduce", command);
        Assert.DoesNotContain("NCCL_P2P_DISABLE", command);
    }

    [Fact]
    public void SingleGpuDefaults_OmitTensorParallelAndDeviceFlags()
    {
        var command = Build(new SlopworksConfig()).BuildRunCommand(GpuProfile, "org/model");

        Assert.DoesNotContain("--tensor-parallel-size", command);
        Assert.DoesNotContain("CUDA_VISIBLE_DEVICES", command);
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
    public async Task SnapshotLogs_WritesRealOutputToFile_AndReturnsIt()
    {
        var dir = Directory.CreateTempSubdirectory("slopworks-log-").FullName;
        try
        {
            var paths = new SlopworksPaths(dir);
            var controller = new VllmServerController(
                new WslLinuxCommandFactory(SlopworksPaths.DistroName), new SlopworksConfig(), new HttpClient(), paths);
            var runner = new FakeProcessRunner
            {
                Result = new Slopworks.Platform.Abstractions.ProcessResult(0, "INFO: starting vLLM\nRoute /v1/models", "", TimeSpan.Zero),
            };

            var text = await controller.SnapshotLogsAsync(runner, 500, CancellationToken.None);

            Assert.Contains("starting vLLM", text);
            var file = Path.Combine(paths.VllmLogsDir, VllmServerController.ServerLogFileName);
            Assert.True(File.Exists(file));
            Assert.Contains("Route /v1/models", await File.ReadAllTextAsync(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SnapshotLogs_ContainerMissing_ReturnsEmpty_AndWritesNothing()
    {
        var dir = Directory.CreateTempSubdirectory("slopworks-log-").FullName;
        try
        {
            var paths = new SlopworksPaths(dir);
            var controller = new VllmServerController(
                new WslLinuxCommandFactory(SlopworksPaths.DistroName), new SlopworksConfig(), new HttpClient(), paths);
            var runner = new FakeProcessRunner
            {
                Result = new Slopworks.Platform.Abstractions.ProcessResult(0, "Error: no such container slopworks-vllm", "", TimeSpan.Zero),
            };

            var text = await controller.SnapshotLogsAsync(runner, 500, CancellationToken.None);

            Assert.Equal("", text);
            Assert.False(File.Exists(Path.Combine(paths.VllmLogsDir, VllmServerController.ServerLogFileName)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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

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
    public void ToolCalling_OnByDefault_AddsAutoToolChoiceAndParser()
    {
        var command = Build(new SlopworksConfig()).BuildRunCommand(GpuProfile, "org/model");

        Assert.Contains("--enable-auto-tool-choice", command);
        Assert.Contains("--tool-call-parser hermes", command);
    }

    [Fact]
    public void ToolCalling_Disabled_OmitsTheFlags()
    {
        var config = new SlopworksConfig();
        config.Server.EnableToolCalling = false;

        var command = Build(config).BuildRunCommand(GpuProfile, "org/model");

        Assert.DoesNotContain("enable-auto-tool-choice", command);
        Assert.DoesNotContain("tool-call-parser", command);
    }

    [Fact]
    public void ToolCallParser_IsConfigurable()
    {
        var config = new SlopworksConfig();
        config.Server.ToolCallParser = "llama3_json";

        var command = Build(config).BuildRunCommand(GpuProfile, "org/model");

        Assert.Contains("--tool-call-parser llama3_json", command);
    }

    [Fact]
    public void Quantization_Auto_OmitsTheFlag()
    {
        var command = Build(new SlopworksConfig()).BuildRunCommand(GpuProfile, "org/model");

        Assert.DoesNotContain("--quantization", command);
    }

    [Theory]
    [InlineData("awq")]
    [InlineData("bitsandbytes")]
    [InlineData("nvfp4")]
    public void Quantization_Explicit_AddsTheFlag(string method)
    {
        var config = new SlopworksConfig();
        config.Server.Quantization = method;

        var command = Build(config).BuildRunCommand(GpuProfile, "org/model");

        Assert.Contains($"--quantization {method}", command);
    }

    [Fact]
    public void LogLevel_IsPassedAsEnvVar()
    {
        var config = new SlopworksConfig();
        config.Server.VllmLogLevel = "DEBUG";

        var command = Build(config).BuildRunCommand(GpuProfile, "org/model");

        Assert.Contains("-e VLLM_LOGGING_LEVEL=DEBUG", command);
    }

    [Theory]
    [InlineData("hf.co/unsloth/Qwen3-32B-AWQ", "unsloth/Qwen3-32B-AWQ")]
    [InlineData("https://huggingface.co/org/model", "org/model")]
    [InlineData("  org/model  ", "org/model")]
    [InlineData("org/model", "org/model")]
    public void ModelId_Normalize_StripsHostPrefixes(string input, string expected)
        => Assert.Equal(expected, ModelId.Normalize(input));

    [Fact]
    public void ModelId_ToleratesNull()
    {
        // A ComboBox can transiently write null into the bound model while its items rebuild.
        Assert.Equal("", ModelId.Normalize(null));
        Assert.Null(ModelId.Advisory(null));
    }

    [Fact]
    public void BuildRunCommand_NormalizesPastedHfCoPrefix()
    {
        var command = Build(new SlopworksConfig()).BuildRunCommand(GpuProfile, "hf.co/org/model");

        Assert.Contains("--model org/model", command);
        Assert.DoesNotContain("hf.co/", command);
    }

    [Theory]
    [InlineData("org/model", false)]
    [InlineData("org/model-AWQ", false)]
    [InlineData("unsloth/Qwen3-27B-GGUF", true)]        // GGUF-only repo
    [InlineData("org/model:q4", true)]                  // Ollama tag
    [InlineData("hf.co/org/model-GGUF", true)]          // prefix stripped, still GGUF
    [InlineData("mlx-community/Qwen3-27B-8bit", true)]  // MLX / Apple Silicon
    public void ModelId_Advisory_FlagsOllamaAndGguf(string input, bool expectWarning)
        => Assert.Equal(expectWarning, ModelId.Advisory(input) is not null);

    [Fact]
    public void MaxModelLen_WhenSet_AddsTheFlag()
    {
        var config = new SlopworksConfig();
        config.Server.MaxModelLen = 8192;

        var command = Build(config).BuildRunCommand(GpuProfile, "org/model");

        Assert.Contains("--max-model-len 8192", command);
    }

    [Fact]
    public void MaxModelLen_Unset_OmitsTheFlag()
    {
        var command = Build(new SlopworksConfig()).BuildRunCommand(GpuProfile, "org/model");

        Assert.DoesNotContain("--max-model-len", command);
    }

    [Fact]
    public void MaxModelLen_DefersToExtraArgs_NoDuplicateFlag()
    {
        var config = new SlopworksConfig();
        config.Server.MaxModelLen = 8192;
        config.Server.ExtraArgs = ["--max-model-len 4096"]; // user's explicit value wins

        var command = Build(config).BuildRunCommand(GpuProfile, "org/model");

        Assert.Contains("--max-model-len 4096", command);
        Assert.DoesNotContain("--max-model-len 8192", command);
    }

    [Theory]
    [InlineData("fp8")]
    [InlineData("fp8_e5m2")]
    public void KvCacheDtype_WhenSet_AddsTheFlag(string dtype)
    {
        var config = new SlopworksConfig();
        config.Server.KvCacheDtype = dtype;

        var command = Build(config).BuildRunCommand(GpuProfile, "org/model");

        Assert.Contains($"--kv-cache-dtype {dtype}", command);
    }

    [Fact]
    public void KvCacheDtype_Auto_OmitsTheFlag()
    {
        var command = Build(new SlopworksConfig()).BuildRunCommand(GpuProfile, "org/model");

        Assert.DoesNotContain("--kv-cache-dtype", command);
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

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
    public void ManagedFlag_NotDuplicated_WhenUserSuppliesItInExtraArgs()
    {
        var config = new SlopworksConfig();
        config.Server.Quantization = "awq";
        config.Server.TensorParallelSize = 2;
        config.Server.ExtraArgs = ["--quantization gptq", "--tensor-parallel-size 4"];

        var command = Build(config).BuildRunCommand(GpuProfile, "org/model");

        // The user's explicit values win; the managed flags aren't emitted a second time.
        Assert.DoesNotContain("--quantization awq", command);
        Assert.DoesNotContain("--tensor-parallel-size 2", command);
        Assert.Contains("--quantization gptq", command);
        Assert.Contains("--tensor-parallel-size 4", command);
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

    [Fact]
    public void Defaults_OmitAllOptionalTuningFlags()
    {
        var command = Build(new SlopworksConfig()).BuildRunCommand(GpuProfile, "org/model");

        Assert.DoesNotContain("--dtype", command);
        Assert.DoesNotContain("--trust-remote-code", command);
        Assert.DoesNotContain("--enforce-eager", command);
        Assert.DoesNotContain("--max-num-seqs", command);
        Assert.DoesNotContain("--max-num-batched-tokens", command);
        Assert.DoesNotContain("prefix-caching", command);
        Assert.DoesNotContain("--served-model-name", command);
    }

    [Theory]
    [InlineData("bfloat16")]
    [InlineData("float16")]
    public void Dtype_Explicit_AddsTheFlag(string dtype)
    {
        var config = new SlopworksConfig();
        config.Server.Dtype = dtype;

        var command = Build(config).BuildRunCommand(GpuProfile, "org/model");

        Assert.Contains($"--dtype {dtype}", command);
    }

    [Fact]
    public void Dtype_Auto_OmitsTheFlag()
    {
        var config = new SlopworksConfig();
        config.Server.Dtype = "auto";

        var command = Build(config).BuildRunCommand(GpuProfile, "org/model");

        Assert.DoesNotContain("--dtype", command);
    }

    [Fact]
    public void Dtype_DefersToExtraArgs_NoDuplicateFlag()
    {
        var config = new SlopworksConfig();
        config.Server.Dtype = "bfloat16";
        config.Server.ExtraArgs = ["--dtype float16"]; // user's explicit value wins

        var command = Build(config).BuildRunCommand(GpuProfile, "org/model");

        Assert.Contains("--dtype float16", command);
        Assert.DoesNotContain("--dtype bfloat16", command);
    }

    [Fact]
    public void TrustRemoteCode_WhenSet_AddsTheFlag()
    {
        var config = new SlopworksConfig();
        config.Server.TrustRemoteCode = true;

        var command = Build(config).BuildRunCommand(GpuProfile, "org/model");

        Assert.Contains("--trust-remote-code", command);
    }

    [Fact]
    public void EnforceEager_WhenSet_AddsTheFlag()
    {
        var config = new SlopworksConfig();
        config.Server.EnforceEager = true;

        var command = Build(config).BuildRunCommand(GpuProfile, "org/model");

        Assert.Contains("--enforce-eager", command);
    }

    [Fact]
    public void MaxNumSeqs_WhenSet_AddsTheFlag_AndDefersToExtraArgs()
    {
        var config = new SlopworksConfig();
        config.Server.MaxNumSeqs = 2;

        Assert.Contains("--max-num-seqs 2", Build(config).BuildRunCommand(GpuProfile, "org/model"));

        config.Server.ExtraArgs = ["--max-num-seqs 8"]; // user's explicit value wins
        var command = Build(config).BuildRunCommand(GpuProfile, "org/model");
        Assert.Contains("--max-num-seqs 8", command);
        Assert.DoesNotContain("--max-num-seqs 2", command);
    }

    [Fact]
    public void MaxNumBatchedTokens_WhenSet_AddsTheFlag()
    {
        var config = new SlopworksConfig();
        config.Server.MaxNumBatchedTokens = 4128;

        var command = Build(config).BuildRunCommand(GpuProfile, "org/model");

        Assert.Contains("--max-num-batched-tokens 4128", command);
    }

    [Fact]
    public void PrefixCaching_True_AddsEnable_False_AddsNoEnable_Null_Omits()
    {
        var on = new SlopworksConfig();
        on.Server.EnablePrefixCaching = true;
        var onCmd = Build(on).BuildRunCommand(GpuProfile, "org/model");
        Assert.Contains("--enable-prefix-caching", onCmd);
        Assert.DoesNotContain("--no-enable-prefix-caching", onCmd);

        var off = new SlopworksConfig();
        off.Server.EnablePrefixCaching = false;
        Assert.Contains("--no-enable-prefix-caching", Build(off).BuildRunCommand(GpuProfile, "org/model"));

        // null (default) leaves vLLM's own default — neither flag.
        Assert.DoesNotContain("prefix-caching", Build(new SlopworksConfig()).BuildRunCommand(GpuProfile, "org/model"));
    }

    [Fact]
    public void ServedModelName_WhenSet_AddsTheFlag()
    {
        var config = new SlopworksConfig();
        config.Server.ServedModelName = "local";

        var command = Build(config).BuildRunCommand(GpuProfile, "org/model");

        Assert.Contains("--served-model-name local", command);
    }

    [Fact]
    public void OptionalTuningFlags_AlsoApplyInCpuMode()
    {
        var config = new SlopworksConfig();
        config.Server.EnforceEager = true;
        config.Server.Dtype = "bfloat16";

        var command = Build(config).BuildRunCommand(new SystemProfile(), "org/model");

        Assert.Contains("--enforce-eager", command);
        Assert.Contains("--dtype bfloat16", command);
    }

    /// <summary>
    /// Builds a controller whose data root has the given template on disk and links a model to it in
    /// the library (models.json) — which is how the controller resolves the model's --chat-template.
    /// Pass templateContent=null to link a model to a template name whose file does NOT exist.
    /// </summary>
    private static (VllmServerController controller, string dir) BuildWithModelTemplate(
        SlopworksConfig config, string modelId, string? templateName, string? templateContent)
    {
        var dir = Directory.CreateTempSubdirectory("slopworks-tpl-").FullName;
        var paths = new SlopworksPaths(dir);
        if (templateName is not null && templateContent is not null)
            new TemplateStore(paths).Create(templateName, templateContent);
        var store = new ModelLibraryStore(paths);
        var doc = store.Load();
        doc.Models.Add(new ModelEntry { Id = modelId, ChatTemplate = templateName });
        store.Save(doc);
        return (new VllmServerController(new WslLinuxCommandFactory(SlopworksPaths.DistroName), config, new HttpClient(), paths), dir);
    }

    [Fact]
    public void ChatTemplate_WhenModelHasOneAndFileExists_MountsItAndPassesTheFlag()
    {
        var (controller, dir) = BuildWithModelTemplate(new SlopworksConfig(), "org/model", "qwen-fixed", "{{ messages }}");
        try
        {
            var command = controller.BuildRunCommand(GpuProfile, "org/model");

            Assert.Contains("--chat-template", command);
            Assert.Contains("qwen-fixed.jinja", command);
            Assert.Contains(":ro", command); // mounted read-only
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ChatTemplate_MissingFile_OmitsMountAndFlag()
    {
        // The model references a template whose file isn't on disk — a dangling reference must not break the run.
        var (controller, dir) = BuildWithModelTemplate(new SlopworksConfig(), "org/model", "does-not-exist", null);
        try
        {
            var command = controller.BuildRunCommand(GpuProfile, "org/model");

            Assert.DoesNotContain("--chat-template", command);
            Assert.DoesNotContain("does-not-exist", command);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ChatTemplate_ModelWithoutOne_OmitsTheFlag()
    {
        var (controller, dir) = BuildWithModelTemplate(new SlopworksConfig(), "org/model", null, null);
        try
        {
            Assert.DoesNotContain("--chat-template", controller.BuildRunCommand(GpuProfile, "org/model"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ChatTemplate_ResolvesPerModel_NotForOtherModels()
    {
        var dir = Directory.CreateTempSubdirectory("slopworks-tpl-").FullName;
        try
        {
            var paths = new SlopworksPaths(dir);
            new TemplateStore(paths).Create("t-a", "a");
            var store = new ModelLibraryStore(paths);
            var doc = store.Load();
            doc.Models.Add(new ModelEntry { Id = "org/a", ChatTemplate = "t-a" });
            doc.Models.Add(new ModelEntry { Id = "org/b", ChatTemplate = null });
            store.Save(doc);
            var controller = new VllmServerController(new WslLinuxCommandFactory(SlopworksPaths.DistroName), new SlopworksConfig(), new HttpClient(), paths);

            Assert.Contains("t-a.jinja", controller.BuildRunCommand(GpuProfile, "org/a"));
            Assert.DoesNotContain("--chat-template", controller.BuildRunCommand(GpuProfile, "org/b"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ChatTemplate_DefersToExtraArgs_NoDuplicateFlag()
    {
        var config = new SlopworksConfig();
        config.Server.ExtraArgs = ["--chat-template /my/own.jinja"]; // user's explicit value wins
        var (controller, dir) = BuildWithModelTemplate(config, "org/model", "qwen-fixed", "body");
        try
        {
            var command = controller.BuildRunCommand(GpuProfile, "org/model");

            Assert.Contains("--chat-template /my/own.jinja", command);
            Assert.DoesNotContain("qwen-fixed.jinja", command); // managed flag suppressed
        }
        finally { Directory.Delete(dir, true); }
    }
}

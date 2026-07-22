using Slopworks.Core.Server;
using Xunit;

namespace Slopworks.Core.Tests;

public class ModelInspectorTests
{
    private static ModelProbe Probe(bool found, string[] files, string[] tags, string? config)
        => new(found, files, tags, config);

    [Fact]
    public void NotFound_IsUnservable()
    {
        var result = ModelConfigClassifier.Classify("org/nope", Probe(false, [], [], null));

        Assert.Equal(ModelVerdict.Unservable, result.Verdict);
        Assert.Contains("not found", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GgufOnlyRepo_IsUnservable_AndPointsToOllama()
    {
        // GGUF repos have no config.json and only .gguf files (plus an mmproj for vision).
        var files = new[]
        {
            "model-IQ4_XS.gguf", "model-Q8_K.gguf", "mmproj-f16.gguf", "README.md",
        };

        var result = ModelConfigClassifier.Classify("HauhauCS/Qwen3.6-27B", Probe(true, files, [], null));

        Assert.Equal(ModelVerdict.Unservable, result.Verdict);
        Assert.Contains("GGUF", result.Summary);
        Assert.Contains("Ollama", result.Detail);
        Assert.Contains("mmproj", result.Detail); // notes the vision projector
    }

    [Fact]
    public void MlxByTag_IsUnservable()
    {
        var files = new[] { "config.json", "model.safetensors" };

        var result = ModelConfigClassifier.Classify("petergilani/Qwen3.6-27B-8bit",
            Probe(true, files, ["mlx", "mlx-lm", "8bit"], """{"architectures":["Qwen3_5ForConditionalGeneration"]}"""));

        Assert.Equal(ModelVerdict.Unservable, result.Verdict);
        Assert.Contains("MLX", result.Summary);
    }

    [Fact]
    public void MlxByConfig_WithoutTag_IsUnservable()
    {
        // The petergilani case: no "mlx" in the id/tags a caller might have, but the config gives it away.
        var config = """{"quantization":{"group_size":64,"bits":8,"mode":"affine"},"model_type":"qwen3_5"}""";

        var result = ModelConfigClassifier.Classify("someone/model-8bit",
            Probe(true, ["config.json", "model.safetensors"], [], config));

        Assert.Equal(ModelVerdict.Unservable, result.Verdict);
        Assert.Contains("MLX", result.Summary);
    }

    [Fact]
    public void AwqSafetensors_IsServable_AndReportsMethod()
    {
        var config = """{"architectures":["Qwen2ForCausalLM"],"model_type":"qwen2","quantization_config":{"quant_method":"awq","bits":4}}""";

        var result = ModelConfigClassifier.Classify("Qwen/Qwen2.5-32B-Instruct-AWQ",
            Probe(true, ["config.json", "model-00001-of-00002.safetensors"], [], config));

        Assert.Equal(ModelVerdict.Servable, result.Verdict);
        Assert.Contains("awq", result.Summary);
    }

    [Fact]
    public void FullPrecisionSafetensors_IsServable()
    {
        var config = """{"architectures":["Qwen2ForCausalLM"],"model_type":"qwen2","torch_dtype":"bfloat16"}""";

        var result = ModelConfigClassifier.Classify("Qwen/Qwen2.5-7B-Instruct",
            Probe(true, ["config.json", "model.safetensors"], [], config));

        Assert.Equal(ModelVerdict.Servable, result.Verdict);
        Assert.Contains("full-precision", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownQuantMethod_IsCaution()
    {
        var config = """{"quantization_config":{"quant_method":"some_new_thing"}}""";

        var result = ModelConfigClassifier.Classify("org/model",
            Probe(true, ["config.json", "model.safetensors"], [], config));

        Assert.Equal(ModelVerdict.Caution, result.Verdict);
    }

    [Fact]
    public void HybridArchitecture_DowngradesToCaution()
    {
        var config = """{"architectures":["Qwen3_5ForConditionalGeneration"],"model_type":"qwen3_5","mamba_ssm_dtype":"float32"}""";

        var result = ModelConfigClassifier.Classify("Qwen/Qwen3.5-27B",
            Probe(true, ["config.json", "model.safetensors"], [], config));

        Assert.Equal(ModelVerdict.Caution, result.Verdict);
        Assert.Contains("hybrid", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoWeightsNoConfig_IsUnservable()
    {
        var result = ModelConfigClassifier.Classify("org/empty", Probe(true, ["README.md"], [], null));

        Assert.Equal(ModelVerdict.Unservable, result.Verdict);
    }

    [Fact]
    public void ParseModelApi_ExtractsFilesAndTags()
    {
        var json = """
        {
          "id": "org/model",
          "tags": ["text-generation", "mlx"],
          "siblings": [
            {"rfilename": "config.json"},
            {"rfilename": "model.safetensors"},
            {"rfilename": "README.md"}
          ]
        }
        """;

        var (files, tags) = ModelInspector.ParseModelApi(json);

        Assert.Equal(["config.json", "model.safetensors", "README.md"], files);
        Assert.Contains("mlx", tags);
    }

    [Fact]
    public void ParseModelApi_MalformedJson_ReturnsEmpty()
    {
        var (files, tags) = ModelInspector.ParseModelApi("not json at all");

        Assert.Empty(files);
        Assert.Empty(tags);
    }
}

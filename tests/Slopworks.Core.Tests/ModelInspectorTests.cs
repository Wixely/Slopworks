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
    public void ParseModelApi_ExtractsFilesTagsParamsAndFacts()
    {
        var json = """
        {
          "id": "org/model",
          "tags": ["text-generation", "mlx", "license:apache-2.0"],
          "pipeline_tag": "image-text-to-text",
          "downloads": 123456,
          "gated": "manual",
          "safetensors": { "parameters": { "BF16": 32763876352 }, "total": 32763876352 },
          "siblings": [
            {"rfilename": "config.json"},
            {"rfilename": "model.safetensors"},
            {"rfilename": "README.md"}
          ]
        }
        """;

        var meta = ModelInspector.ParseModelApi(json);

        Assert.Equal(["config.json", "model.safetensors", "README.md"], meta.Files);
        Assert.Contains("mlx", meta.Tags);
        Assert.Equal(32763876352, meta.Parameters);
        Assert.Equal("image-text-to-text", meta.Pipeline);
        Assert.Equal("apache-2.0", meta.License);
        Assert.True(meta.Gated);
        Assert.Equal(123456, meta.Downloads);
    }

    [Fact]
    public void ParseModelApi_MalformedJson_ReturnsEmpty()
    {
        var meta = ModelInspector.ParseModelApi("not json at all");

        Assert.Empty(meta.Files);
        Assert.Empty(meta.Tags);
        Assert.Null(meta.Parameters);
        Assert.False(meta.Gated);
    }

    [Fact]
    public void Classify_SurfacesAllMetadata()
    {
        var config = """
        {"architectures":["Qwen2ForCausalLM"],"quantization_config":{"quant_method":"awq"},
         "max_position_embeddings":32768,"torch_dtype":"bfloat16"}
        """;
        var probe = new ModelProbe(true, ["config.json", "model.safetensors"], [], config,
            Parameters: 32_763_876_352, Pipeline: "text-generation", License: "apache-2.0", Gated: true, Downloads: 999);

        var result = ModelConfigClassifier.Classify("Qwen/Qwen2.5-32B-AWQ", probe);

        Assert.Equal("awq", result.Quant);
        Assert.Equal("Qwen2ForCausalLM", result.Architecture);
        Assert.Equal("32.8B", result.ParametersText);
        Assert.Equal(32768, result.MaxContext);
        Assert.Equal("bfloat16", result.Dtype);
        Assert.Equal("text-generation", result.Pipeline);
        Assert.Equal("apache-2.0", result.License);
        Assert.True(result.Gated);
        Assert.Equal(999, result.Downloads);
    }

    [Fact]
    public void ParametersText_FormatsMillionsAndUnknown()
    {
        Assert.Equal("—", new ModelInspection(ModelVerdict.Unknown, "", "").ParametersText);
        Assert.Equal("494M", new ModelInspection(ModelVerdict.Servable, "", "") { Parameters = 494_000_000 }.ParametersText);
    }
}

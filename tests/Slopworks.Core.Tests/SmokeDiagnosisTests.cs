using Slopworks.Core.Steps;
using Xunit;

namespace Slopworks.Core.Tests;

public class SmokeDiagnosisTests
{
    [Fact]
    public void ExtractRootCause_StartsAtTheEarliestErrorSignature_NotTheDownstreamWrapper()
    {
        var log = string.Join('\n',
            "INFO: starting engine",
            "INFO: loading weights",
            "RuntimeError: CUDA error: no kernel image is available for execution on the device",
            "  ... torch stack ...",
            "Traceback (most recent call last):",
            "  File core_client.py line 573",
            "RuntimeError: Engine core initialization failed. Failed core proc(s): {}");

        var excerpt = SmokeTestStepDiagnosis(log);

        // The real cause is present, and it appears before the downstream wrapper.
        Assert.Contains("no kernel image is available", excerpt);
        var causeIdx = excerpt.IndexOf("no kernel image", System.StringComparison.Ordinal);
        var wrapperIdx = excerpt.IndexOf("Engine core initialization failed", System.StringComparison.Ordinal);
        Assert.True(causeIdx >= 0 && causeIdx < wrapperIdx);
    }

    [Fact]
    public void ExtractRootCause_NoMarkers_ReturnsTail()
    {
        var log = string.Concat(System.Linq.Enumerable.Repeat("plain progress line\n", 500));

        var excerpt = SmokeTestStepDiagnosis(log);

        Assert.StartsWith("…", excerpt);
        Assert.Contains("plain progress line", excerpt);
    }

    [Fact]
    public void DiagnoseHint_PreQuantizedMethodOnBaseModel_ExplainsTheConfigFileError()
    {
        // The real error from setting Quantization=awq/gptq on a full-precision repo.
        var awq = DiagnoseHint("pydantic_core ValidationError: Value error, Cannot find the config file for awq");
        var gptq = DiagnoseHint("Value error, Cannot find the config file for gptq [type=value_error]");

        Assert.Contains("awq", awq);
        Assert.Contains("Quantization = auto", awq);
        Assert.Contains("bitsandbytes", awq);
        Assert.Contains("gptq", gptq); // the method name is echoed back
    }

    [Fact]
    public void DiagnoseHint_BitsAndBytesUnderTensorParallel_PointsToAwqOrSingleGpu()
    {
        var log = string.Join('\n',
            "(Worker_TP0 pid=577) ERROR bitsandbytes_loader.py load_weights",
            "(Worker_TP1 pid=578) ERROR WorkerProc failed to start",
            "  File linear.py line 703 in weight_loader");

        var hint = DiagnoseHint(log);

        Assert.Contains("shard", hint);
        Assert.Contains("Split across", hint);
    }

    [Fact]
    public void DiagnoseHint_GgufRepo_PointsToOllama()
    {
        var hint = DiagnoseHint("ValidationError: ensure the presence of a 'config.json'");

        Assert.Contains("GGUF", hint);
        Assert.Contains("Ollama", hint);
    }

    [Theory]
    [InlineData("Cannot find the config file for awq", "awq")]
    [InlineData("Cannot find the config file for gptq [type=value_error]", "gptq")]
    [InlineData("Cannot find the config file for compressed-tensors.", "compressed-tensors")]
    [InlineData("no marker here", "")]
    public void ExtractAfter_TakesTheTokenAfterTheMarker(string text, string expected)
    {
        var method = typeof(VllmSmokeTestStep).GetMethod(
            "ExtractAfter",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.Equal(expected, (string)method.Invoke(null, [text, "Cannot find the config file for "])!);
    }

    // These internals aren't public (InternalsVisibleTo isn't set), so exercise them via reflection.
    private static string SmokeTestStepDiagnosis(string log)
    {
        var method = typeof(VllmSmokeTestStep).GetMethod(
            "ExtractRootCause",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (string)method.Invoke(null, [log, 3000])!;
    }

    private static string DiagnoseHint(string log)
    {
        var method = typeof(VllmSmokeTestStep).GetMethod(
            "DiagnoseHint",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (string)method.Invoke(null, [log])!;
    }
}

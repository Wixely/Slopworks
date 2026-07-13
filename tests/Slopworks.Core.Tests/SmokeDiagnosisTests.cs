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

    // ExtractRootCause is internal; InternalsVisibleTo isn't set, so exercise it via reflection.
    private static string SmokeTestStepDiagnosis(string log)
    {
        var method = typeof(VllmSmokeTestStep).GetMethod(
            "ExtractRootCause",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (string)method.Invoke(null, [log, 3000])!;
    }
}

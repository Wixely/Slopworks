using Slopworks.Core.Platform;
using Xunit;

namespace Slopworks.Core.Tests;

public class CpuMathTests
{
    [Theory]
    [InlineData(0ul, 100ul, 100.0)]   // no idle time → fully busy
    [InlineData(100ul, 100ul, 0.0)]   // all idle → 0%
    [InlineData(50ul, 100ul, 50.0)]
    [InlineData(0ul, 0ul, 0.0)]       // no elapsed interval → 0, not NaN
    public void Percent_ComputesBusyShare(ulong idleDelta, ulong totalDelta, double expected)
        => Assert.Equal(expected, CpuMath.Percent(idleDelta, totalDelta), precision: 5);

    [Fact]
    public void Percent_ClampsWhenIdleExceedsTotal()
        => Assert.Equal(0.0, CpuMath.Percent(150, 100));
}

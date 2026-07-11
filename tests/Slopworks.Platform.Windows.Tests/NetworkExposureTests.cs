using Slopworks.Platform.Windows;
using Xunit;

namespace Slopworks.Platform.Windows.Tests;

public class NetworkExposureTests
{
    private const string NetshShowOutput = """

        Listen on ipv4:             Connect to ipv4:

        Address         Port        Address         Port
        --------------- ----------  --------------- ----------
        0.0.0.0         8000        127.0.0.1       8000
        0.0.0.0         9999        192.168.1.50    80
        """;

    [Fact]
    public void HasProxyForPort_FindsOurForward()
        => Assert.True(WindowsNetworkExposure.HasProxyForPort(NetshShowOutput, 8000));

    [Fact]
    public void HasProxyForPort_IgnoresForwardsToOtherTargets()
        => Assert.False(WindowsNetworkExposure.HasProxyForPort(NetshShowOutput, 9999));

    [Fact]
    public void HasProxyForPort_FalseOnEmptyTable()
        => Assert.False(WindowsNetworkExposure.HasProxyForPort("Listen on ipv4:  Connect to ipv4:", 8000));

    [Fact]
    public void EnableCommands_AreAdditiveAndPortScopedOnly()
    {
        var description = new WindowsNetworkExposure().DescribeEnable(8000);

        // Strictly one portproxy and one firewall rule, both scoped to the exact port.
        Assert.Contains("listenaddress=0.0.0.0 listenport=8000", description);
        Assert.Contains("connectaddress=127.0.0.1 connectport=8000", description);
        Assert.Contains("localport=8000", description);
        // Never anything that could disturb global networking.
        Assert.DoesNotContain("set ", description);
        Assert.DoesNotContain("winsock", description);
        Assert.DoesNotContain("reset", description);
    }
}

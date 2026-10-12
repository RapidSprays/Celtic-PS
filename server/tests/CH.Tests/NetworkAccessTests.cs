using System.Net;
using CH.Server;

namespace CH.Tests;

public sealed class NetworkAccessTests
{
    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.101.102.103", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("100.63.255.255", false)]
    [InlineData("192.168.1.20", false)]
    [InlineData("fd7a:115c:a1e0::1", true)]
    [InlineData("fd7a:115c:a1e1::1", false)]
    [InlineData("::ffff:100.70.1.2", true)]
    public void Tailscale_ranges(string address, bool expected) =>
        Assert.Equal(expected, NetworkAccess.IsTailscale(IPAddress.Parse(address)));

    [Fact]
    public void Tailscale_mode_admits_only_this_machine_and_the_tailnet()
    {
        var open = new ServerOptions();
        Assert.True(open.IsAllowedClient(IPAddress.Parse("192.168.1.20")));

        var tailnet = new ServerOptions { TailscaleOnly = true };
        Assert.True(tailnet.IsAllowedClient(IPAddress.Loopback));
        Assert.True(tailnet.IsAllowedClient(IPAddress.Parse("100.90.1.2")));
        Assert.False(tailnet.IsAllowedClient(IPAddress.Parse("8.8.8.8")));
        Assert.False(tailnet.IsAllowedClient(null));
    }
}

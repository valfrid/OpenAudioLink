using System.Net;
using OpenAudioLink.Core.Net;
using Xunit;

namespace OpenAudioLink.Core.Tests;

/// <summary>
/// Which network OpenAudioLink runs on, decided once instead of five times.
/// </summary>
/// <remarks>
/// Written after a Hub on Tailscale announced Spotify on the overlay: the
/// cast point appeared on the phone, answered when prodded, and never
/// played. The case that matters most below is the one where a perfectly
/// reachable interface is still the wrong one.
/// </remarks>
public class OalNetworkTests
{
    private static LocalAddress Local(string address, int prefix) =>
        new(IPAddress.Parse(address), prefix);

    private static readonly LocalAddress[] HubWithTailscale =
    [
        Local("100.96.246.85", 32),
        Local("192.168.0.201", 24),
    ];

    /// <summary>
    /// The whole point: a node is on one network, so the node decides.
    /// </summary>
    [Fact]
    public void TheNetworkIsWhereTheDevicesAre()
    {
        var result = OalNetworkResolver.Resolve(
            null, HubWithTailscale, [IPAddress.Parse("192.168.0.71")]);

        Assert.True(result.Resolved);
        Assert.Equal("192.168.0.201/24", result.Network!.ToString());
    }

    /// <summary>
    /// The overlay is listed first and is genuinely reachable — which is
    /// what made this hard to see — and still loses, because no speaker
    /// has ever announced from it.
    /// </summary>
    [Fact]
    public void AReachableOverlayWithNoDevicesOnItIsNotTheNetwork()
    {
        var result = OalNetworkResolver.Resolve(
            null, HubWithTailscale,
            [IPAddress.Parse("192.168.0.71"), IPAddress.Parse("192.168.0.237")]);

        Assert.Equal("192.168.0.201/24", result.Network!.ToString());
    }

    /// <summary>A stray device elsewhere does not move the whole system.</summary>
    [Fact]
    public void TheSubnetWithMostDevicesWins()
    {
        LocalAddress[] locals = [Local("10.0.0.5", 24), Local("192.168.0.201", 24)];
        var result = OalNetworkResolver.Resolve(
            null, locals,
            [
                IPAddress.Parse("192.168.0.71"),
                IPAddress.Parse("192.168.0.237"),
                IPAddress.Parse("10.0.0.9"),
            ]);

        Assert.Equal("192.168.0.201/24", result.Network!.ToString());
    }

    [Fact]
    public void ConfigurationBeatsTheDevices()
    {
        var result = OalNetworkResolver.Resolve(
            "10.0.0.5", [Local("10.0.0.5", 24), Local("192.168.0.201", 24)],
            [IPAddress.Parse("192.168.0.71")]);

        Assert.Equal("10.0.0.5/24", result.Network!.ToString());
    }

    [Fact]
    public void ConfigurationMayNameTheSubnetInsteadOfTheAddress()
    {
        var result = OalNetworkResolver.Resolve(
            "192.168.0.0/24", HubWithTailscale, []);

        Assert.Equal("192.168.0.201/24", result.Network!.ToString());
    }

    /// <summary>A configured network this machine is not on is a mistake worth naming.</summary>
    [Fact]
    public void ConfigurationThisMachineIsNotOnIsRefusedWithItsAddresses()
    {
        var result = OalNetworkResolver.Resolve("172.16.9.9", HubWithTailscale, []);

        Assert.False(result.Resolved);
        Assert.Contains("192.168.0.201", result.Explanation);
    }

    [Fact]
    public void NonsenseConfigurationIsRefusedRatherThanIgnored()
    {
        var result = OalNetworkResolver.Resolve("the living room", HubWithTailscale, []);

        Assert.False(result.Resolved);
        Assert.Contains("not an address or a subnet", result.Explanation);
    }

    /// <summary>
    /// Before any device has been heard from there is nothing to observe,
    /// and one private network is a safe provisional answer.
    /// </summary>
    [Fact]
    public void WithNoDevicesYetASinglePrivateNetworkIsUsedProvisionally()
    {
        var result = OalNetworkResolver.Resolve(null, HubWithTailscale, []);

        Assert.Equal("192.168.0.201/24", result.Network!.ToString());
        Assert.Contains("No device has announced yet", result.Explanation);
    }

    /// <summary>Two plausible LANs and no evidence is a question, not a guess.</summary>
    [Fact]
    public void TwoPrivateNetworksAndNoDevicesIsUnresolved()
    {
        var result = OalNetworkResolver.Resolve(
            null, [Local("10.0.0.5", 24), Local("192.168.0.201", 24)], []);

        Assert.False(result.Resolved);
        Assert.Contains("Hub:Network", result.Explanation);
    }

    [Theory]
    [InlineData("192.168.0.71", true)]
    [InlineData("192.168.0.255", true)]
    [InlineData("192.168.1.71", false)]
    [InlineData("100.96.246.85", false)]
    public void ContainsFollowsThePrefix(string address, bool expected)
    {
        var network = new OalNetwork(IPAddress.Parse("192.168.0.201"), 24);
        Assert.Equal(expected, network.Contains(IPAddress.Parse(address)));
    }
}

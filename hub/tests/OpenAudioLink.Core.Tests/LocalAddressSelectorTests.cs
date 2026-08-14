using System.Net;
using OpenAudioLink.Core.Net;
using Xunit;

namespace OpenAudioLink.Core.Tests;

public class LocalAddressSelectorTests
{
    private static LocalAddress Addr(string address, int prefix) =>
        new(IPAddress.Parse(address), prefix);

    /// <summary>
    /// The case that broke OTA in the field: a Hub with both a LAN address
    /// and a Tailscale CGNAT address told a node on the LAN to fetch
    /// firmware from the CGNAT address, which does not exist on its network.
    /// </summary>
    [Fact]
    public void Lan_address_wins_over_an_overlay_network()
    {
        var chosen = LocalAddressSelector.SelectSameSubnet(
            IPAddress.Parse("192.168.0.174"),
            [Addr("100.119.101.42", 32), Addr("192.168.0.50", 24)]);

        Assert.Equal(IPAddress.Parse("192.168.0.50"), chosen);
    }

    [Fact]
    public void Order_of_candidates_does_not_matter()
    {
        var chosen = LocalAddressSelector.SelectSameSubnet(
            IPAddress.Parse("192.168.0.174"),
            [Addr("192.168.0.50", 24), Addr("100.119.101.42", 32)]);

        Assert.Equal(IPAddress.Parse("192.168.0.50"), chosen);
    }

    [Fact]
    public void Docker_and_hyperv_bridges_are_passed_over()
    {
        var chosen = LocalAddressSelector.SelectSameSubnet(
            IPAddress.Parse("192.168.0.174"),
            [Addr("172.17.0.1", 16), Addr("172.28.112.1", 20), Addr("192.168.0.50", 24)]);

        Assert.Equal(IPAddress.Parse("192.168.0.50"), chosen);
    }

    /// <summary>
    /// A host on two subnets that both contain the device — take the
    /// narrower one, which is the more specific route to it.
    /// </summary>
    [Fact]
    public void Most_specific_subnet_wins()
    {
        var chosen = LocalAddressSelector.SelectSameSubnet(
            IPAddress.Parse("10.0.1.7"),
            [Addr("10.9.9.9", 8), Addr("10.0.1.5", 24)]);

        Assert.Equal(IPAddress.Parse("10.0.1.5"), chosen);
    }

    [Fact]
    public void No_match_returns_null_so_the_caller_can_fall_back()
    {
        var chosen = LocalAddressSelector.SelectSameSubnet(
            IPAddress.Parse("8.8.8.8"),
            [Addr("192.168.0.50", 24), Addr("100.119.101.42", 32)]);

        Assert.Null(chosen);
    }

    [Fact]
    public void Empty_candidate_list_returns_null()
    {
        Assert.Null(LocalAddressSelector.SelectSameSubnet(IPAddress.Parse("192.168.0.174"), []));
    }

    /// <summary>
    /// A /32 masks with all bits set; a naive shift by 32 wraps to a shift
    /// of 0 on x86 and would match everything.
    /// </summary>
    [Fact]
    public void Host_route_matches_only_itself()
    {
        var candidates = new[] { Addr("203.0.113.9", 32) };

        Assert.Equal(
            IPAddress.Parse("203.0.113.9"),
            LocalAddressSelector.SelectSameSubnet(IPAddress.Parse("203.0.113.9"), candidates));
        Assert.Null(LocalAddressSelector.SelectSameSubnet(IPAddress.Parse("203.0.113.10"), candidates));
    }

    [Fact]
    public void Ipv6_target_is_not_matched_against_ipv4_addresses()
    {
        Assert.Null(LocalAddressSelector.SelectSameSubnet(
            IPAddress.Parse("fe80::1"), [Addr("192.168.0.50", 24)]));
    }

    /// <summary>
    /// IPv6 unicast entries report prefix lengths above 32; they must not be
    /// mistaken for narrow IPv4 subnets.
    /// </summary>
    [Fact]
    public void Ipv6_candidates_are_ignored()
    {
        Assert.Null(LocalAddressSelector.SelectSameSubnet(
            IPAddress.Parse("192.168.0.174"),
            [new LocalAddress(IPAddress.Parse("fe80::1"), 64)]));
    }

    [Fact]
    public void Enumerated_addresses_exclude_loopback_and_link_local()
    {
        foreach (var local in LocalAddressSelector.EnumerateLocalAddresses())
        {
            Assert.False(IPAddress.IsLoopback(local.Address));
            Assert.DoesNotContain("169.254.", local.Address.ToString(), StringComparison.Ordinal);
            Assert.InRange(local.PrefixLength, 0, 32);
        }
    }

    /// <summary>
    /// The range Tailscale and other overlays allocate from is reachable,
    /// routable and real — and still not the LAN. Spotify Connect and
    /// anything else that must stay local has to be able to tell.
    /// </summary>
    [Theory]
    [InlineData("192.168.0.201", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.4.9", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("100.96.246.85", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("172.15.0.1", false)]
    [InlineData("8.8.8.8", false)]
    public void PrivateAddressesAreTheLanOnes(string address, bool expected)
    {
        Assert.Equal(expected, LocalAddressSelector.IsPrivate(IPAddress.Parse(address)));
    }
}

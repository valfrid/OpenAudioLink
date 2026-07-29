using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OpenAudioLink.Core.Net;

/// <summary>One of this host's IPv4 addresses and the size of its subnet.</summary>
public readonly record struct LocalAddress(IPAddress Address, int PrefixLength);

/// <summary>
/// Picks the address a device should use to reach this host.
/// </summary>
/// <remarks>
/// The obvious trick — connect a UDP socket to the device and read the
/// socket's local address — asks the routing table which interface it
/// prefers, which is not the same question. A machine running a VPN or
/// overlay network (Tailscale, ZeroTier, Docker, Hyper-V) has interfaces
/// whose addresses are unreachable from the LAN, and the routing table
/// happily hands one of them back. A node then fetches firmware from an
/// address that does not exist on its network and the transfer fails at
/// connect.
///
/// So match on the subnet instead: of this host's addresses, use one whose
/// network contains the device. That is an address the device can reach by
/// definition, no routing involved. The routing table remains the fallback
/// for the genuinely routed case, where the device is somewhere else
/// entirely and no local subnet contains it.
/// </remarks>
public static class LocalAddressSelector
{
    /// <summary>
    /// Returns the local address whose subnet contains <paramref name="target"/>,
    /// preferring the most specific subnet when several match, or null when
    /// none does.
    /// </summary>
    public static IPAddress? SelectSameSubnet(IPAddress target, IEnumerable<LocalAddress> candidates)
    {
        if (target.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        LocalAddress? best = null;
        foreach (var candidate in candidates)
        {
            if (candidate.Address.AddressFamily != AddressFamily.InterNetwork
                || candidate.PrefixLength is < 1 or > 32
                || !SameNetwork(candidate.Address, target, candidate.PrefixLength))
            {
                continue;
            }

            if (best is null || candidate.PrefixLength > best.Value.PrefixLength)
            {
                best = candidate;
            }
        }

        return best?.Address;
    }

    private static bool SameNetwork(IPAddress a, IPAddress b, int prefixLength)
    {
        var left = ToUInt32(a);
        var right = ToUInt32(b);
        // A /32 shifts by 32, which on x86 wraps to a shift of 0; the
        // conditional keeps the mask well defined at both ends.
        var mask = prefixLength == 32 ? uint.MaxValue : ~(uint.MaxValue >> prefixLength);
        return (left & mask) == (right & mask);
    }

    private static uint ToUInt32(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    /// <summary>This host's usable IPv4 addresses, loopback and link-local excluded.</summary>
    public static IEnumerable<LocalAddress> EnumerateLocalAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork
                    || IPAddress.IsLoopback(unicast.Address)
                    || IsLinkLocal(unicast.Address))
                {
                    continue;
                }

                yield return new LocalAddress(unicast.Address, unicast.PrefixLength);
            }
        }
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return bytes[0] == 169 && bytes[1] == 254;
    }

    /// <summary>
    /// The address to advertise to a device at <paramref name="deviceAddress"/>:
    /// a same-subnet address when this host has one, otherwise whatever the
    /// routing table would use.
    /// </summary>
    public static string ForDevice(string deviceAddress)
    {
        if (!IPAddress.TryParse(deviceAddress, out var target))
        {
            return AskRoutingTable(deviceAddress);
        }

        return SelectSameSubnet(target, EnumerateLocalAddresses())?.ToString()
            ?? AskRoutingTable(deviceAddress);
    }

    private static string AskRoutingTable(string deviceAddress)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect(deviceAddress, 65000);
        return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
    }
}

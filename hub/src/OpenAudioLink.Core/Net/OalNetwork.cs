using System.Net;
using System.Net.Sockets;

namespace OpenAudioLink.Core.Net;

/// <summary>
/// The one network OpenAudioLink runs on: a local address and its prefix.
/// </summary>
/// <remarks>
/// Decision 15. The Hub used to work out which network it was on five
/// separate times — the RTP source socket, librespot's announcement
/// interface, the OTA URL, the discovery announcements, and its own record
/// in the device registry — and on a machine with an overlay network they
/// disagreed. Four found the LAN and one found Tailscale, which was enough
/// to make a Spotify cast point appear on the phone, answer when prodded,
/// and never play.
///
/// So it is decided once and asked for. A fact that must be true in five
/// places should be derived in none of them.
/// </remarks>
public sealed record OalNetwork(IPAddress Address, int PrefixLength)
{
    /// <summary>Whether a device on this address belongs to this network.</summary>
    public bool Contains(IPAddress other)
    {
        if (other.AddressFamily != AddressFamily.InterNetwork
            || Address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var mask = PrefixLength == 0
            ? 0u
            : PrefixLength >= 32 ? uint.MaxValue : ~(uint.MaxValue >> PrefixLength);
        return (ToUInt32(Address) & mask) == (ToUInt32(other) & mask);
    }

    public override string ToString() => $"{Address}/{PrefixLength}";

    private static uint ToUInt32(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
}

/// <summary>What resolving the configured network produced, and why.</summary>
/// <remarks>
/// The explanation is not decoration. The failure this exists to prevent
/// was silent, so the answer — including "I could not tell" — has to reach
/// a log line and the setup page.
/// </remarks>
public sealed record OalNetworkResult(OalNetwork? Network, string Explanation)
{
    public bool Resolved => Network is not null;
}

/// <summary>
/// Works out which network OpenAudioLink runs on.
/// </summary>
/// <remarks>
/// In order of evidence: what the operator configured, then where the
/// devices are, then a guess confined to private addresses.
///
/// The middle one is the answer nearly always, and it is the reason this
/// needs no configuration in a normal house. The Hub may be on several
/// networks — a LAN, a VPN, a container bridge — but an ESP32 is on
/// exactly one and announces itself from it. So the network is observable:
/// it is wherever the speakers are, and an interface with no device on it
/// is not the audio network however reachable it looks.
/// </remarks>
public static class OalNetworkResolver
{
    /// <param name="configured">
    /// An address on the network (<c>192.168.0.201</c>) or the network
    /// itself (<c>192.168.0.0/24</c>). Empty means work it out.
    /// </param>
    /// <param name="locals">This host's addresses.</param>
    /// <param name="nodes">
    /// Addresses OpenAudioLink devices have announced from. These are the
    /// best evidence there is and are used before any heuristic.
    /// </param>
    public static OalNetworkResult Resolve(
        string? configured, IEnumerable<LocalAddress> locals, IEnumerable<IPAddress> nodes)
    {
        var candidates = locals
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                && a.PrefixLength is >= 1 and <= 32)
            .ToList();

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return FromConfiguration(configured.Trim(), candidates);
        }

        return FromNodes(nodes, candidates) ?? Detect(candidates);
    }

    /// <summary>
    /// The network the nodes are on, which is the network by definition.
    /// </summary>
    /// <remarks>
    /// The Hub may sit on several networks; a node sits on exactly one, and
    /// announces itself from it. So the question "which network does
    /// OpenAudioLink run on" has an observed answer and does not need a
    /// configured one: it is wherever the speakers are.
    ///
    /// This is why the overlay problem is solvable without asking the
    /// operator anything. Tailscale, Docker and Hyper-V add interfaces to
    /// the Hub and never to an ESP32, so an interface with no node on it is
    /// not the audio network however reachable it looks.
    ///
    /// Nodes only, never the Hub's own record. Matching this host against
    /// its own address succeeds on any interface and proves nothing, which
    /// is exactly how Spotify came to be announced on a tailnet.
    ///
    /// The most-populated subnet wins if nodes somehow span two, because a
    /// stray device on the wrong network should not move the whole system.
    /// </remarks>
    private static OalNetworkResult? FromNodes(
        IEnumerable<IPAddress> nodes, IReadOnlyList<LocalAddress> candidates)
    {
        var best = candidates
            .Select(local => new
            {
                Local = local,
                Count = nodes.Count(node =>
                    new OalNetwork(local.Address, local.PrefixLength).Contains(node)),
            })
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .ThenByDescending(x => x.Local.PrefixLength)
            .FirstOrDefault();

        if (best is null)
        {
            return null;
        }

        var network = new OalNetwork(best.Local.Address, best.Local.PrefixLength);
        return new OalNetworkResult(
            network,
            $"Using {network}, where {best.Count} OpenAudioLink device"
                + $"{(best.Count == 1 ? " is" : "s are")} announcing.");
    }

    /// <remarks>
    /// Configuration wins outright, including over the private-address
    /// preference below. A machine can legitimately run OpenAudioLink
    /// somewhere this code would not guess, and the operator saying so is
    /// better evidence than any heuristic.
    /// </remarks>
    private static OalNetworkResult FromConfiguration(
        string configured, IReadOnlyList<LocalAddress> candidates)
    {
        var slash = configured.IndexOf('/');
        if (slash < 0)
        {
            if (!IPAddress.TryParse(configured, out var wanted))
            {
                return new OalNetworkResult(
                    null,
                    $"Hub:Network is '{configured}', which is not an address or a subnet.");
            }

            var match = candidates.FirstOrDefault(a => a.Address.Equals(wanted));
            return match.Address is null
                ? new OalNetworkResult(
                    null,
                    $"Hub:Network is {wanted}, which is not an address on this machine. "
                        + $"This machine has {Describe(candidates)}.")
                : new OalNetworkResult(
                    new OalNetwork(match.Address, match.PrefixLength),
                    $"Hub:Network names {match.Address}/{match.PrefixLength}.");
        }

        if (!IPAddress.TryParse(configured[..slash], out var network)
            || !int.TryParse(configured[(slash + 1)..], out var prefix)
            || prefix is < 1 or > 32)
        {
            return new OalNetworkResult(
                null, $"Hub:Network is '{configured}', which is not a subnet in a.b.c.d/n form.");
        }

        // The host's own address on that subnet, because a subnet is where
        // the speakers are and a socket has to bind something specific.
        var subnet = new OalNetwork(network, prefix);
        var onIt = candidates.FirstOrDefault(a => subnet.Contains(a.Address));
        return onIt.Address is null
            ? new OalNetworkResult(
                null,
                $"Hub:Network is {subnet}, and this machine has no address on it. "
                    + $"It has {Describe(candidates)}.")
            : new OalNetworkResult(
                new OalNetwork(onIt.Address, prefix),
                $"Hub:Network is {subnet}; this machine is on it at {onIt.Address}.");
    }

    /// <remarks>
    /// Only reached before any node has been heard from — a fresh Hub, or
    /// one whose speakers are all off. It is a stopgap so that discovery
    /// and the setup page have something to say, not the real answer: the
    /// real answer arrives with the first announcement and replaces this.
    ///
    /// Private addresses only, and exactly one of them. An overlay network
    /// is a real, routable, reachable interface — which is what makes
    /// guessing dangerous — so the guess is confined to RFC 1918 and gives
    /// up rather than choosing between two plausible LANs. Several is a
    /// question for a person, and a Hub that says so beats one that picks
    /// and is wrong for a week.
    /// </remarks>
    private static OalNetworkResult Detect(IReadOnlyList<LocalAddress> candidates)
    {
        var priv = candidates.Where(a => LocalAddressSelector.IsPrivate(a.Address)).ToList();

        return priv.Count switch
        {
            1 => new OalNetworkResult(
                new OalNetwork(priv[0].Address, priv[0].PrefixLength),
                $"No device has announced yet; provisionally using "
                    + $"{priv[0].Address}/{priv[0].PrefixLength}, the only private network on "
                    + "this machine."),
            0 => new OalNetworkResult(
                null,
                "No private network on this machine, so there is nothing to run OpenAudioLink "
                    + $"on. It has {Describe(candidates)}. Set Hub:Network."),
            _ => new OalNetworkResult(
                null,
                $"This machine is on {priv.Count} private networks — {Describe(priv)} — and no "
                    + "device has announced yet to say which one OpenAudioLink is on. Waiting "
                    + "for a device, or set Hub:Network."),
        };
    }

    private static string Describe(IReadOnlyList<LocalAddress> addresses) =>
        addresses.Count == 0
            ? "no usable IPv4 address"
            : string.Join(", ", addresses.Select(a => $"{a.Address}/{a.PrefixLength}"));
}

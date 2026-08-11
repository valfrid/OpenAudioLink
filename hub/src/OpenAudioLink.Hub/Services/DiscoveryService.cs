using System.Net;
using System.Net.Sockets;
using OpenAudioLink.Core.Devices;
using OpenAudioLink.Core.Net;
using OpenAudioLink.Core.Discovery;
using OpenAudioLink.Core.Protocol;
using OpenAudioLink.Hub.Configuration;

namespace OpenAudioLink.Hub.Services;

/// <summary>
/// Control-plane discovery participant (protocol/DISCOVERY.md).
///
/// As Controller it listens for device announces and feeds the registry;
/// as a device it announces the Hub itself every announce interval. On
/// startup it multicasts a probe so already-running devices appear without
/// waiting for their next periodic announce.
/// </summary>
public sealed class DiscoveryService : BackgroundService
{
    public static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often the Hub asks devices to report in. Multicast announces are
    /// unacknowledged and are lost often enough over Wi-Fi to make a device
    /// appear to flap; a probe draws a *unicast* reply, which gets
    /// link-layer retries like any other unicast frame.
    /// </summary>
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);

    private readonly DeviceRegistry _registry;
    private readonly HubConfig _config;
    private readonly ILogger<DiscoveryService> _logger;

    public DiscoveryService(DeviceRegistry registry, HubConfig config, ILogger<DiscoveryService> logger)
    {
        _registry = registry;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Stops Windows reporting an ICMP port-unreachable as a socket error.
    ///
    /// Probing devices directly means sending to a device that may be
    /// rebooting, and a rebooting device answers with ICMP port-unreachable.
    /// Windows then fails the *next* operation on the socket with
    /// WSAECONNRESET — so a node restarting after an update breaks the
    /// socket the Hub uses to hear from every other device. Discovery is
    /// exactly where this bites, because the Hub is obliged to talk to
    /// devices that come and go.
    /// </summary>
    public static void SuppressConnectionReset(Socket socket, ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const int SioUdpConnReset = unchecked((int)0x9800000C);
        try
        {
            socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }
        catch (SocketException ex)
        {
            logger?.LogDebug(ex, "Could not disable UDP connection reset");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var client = new UdpClient();
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, ProtocolSuite.DiscoveryPort));
        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
        SuppressConnectionReset(client.Client, _logger);

        _interfaces = JoinOnEveryInterface(client.Client, _logger);

        /*
         * The Hub is a device too, and until now the only one missing from
         * its own inventory — it announces to nodes and ignores its own
         * announce, so nothing ever put it in the registry. That made the
         * one producer that is always present the one producer nothing could
         * select: internet radio, system audio and the test tone all name it
         * as the producer, and all three were rejected or silently skipped.
         *
         * Registered before the loops start, so it is there for the first
         * request rather than the first announce.
         */
        var self = _registry.UpsertSelf(
            BuildAnnounce(),
            _interfaces.FirstOrDefault() ?? IPAddress.Loopback);
        _logger.LogInformation(
            "Registered this Hub as {Id} ({Name}) at {Address}", self.Id, self.Name, self.Address);

        var groupEndpoint = new IPEndPoint(ProtocolSuite.DiscoveryMulticastGroup, ProtocolSuite.DiscoveryPort);
        await SendToGroupAsync(
            client, new DiscoveryProbe { ProtocolVersion = ProtocolSuite.Version }.Serialize(),
            groupEndpoint, stoppingToken);

        await Task.WhenAll(
            ReceiveLoopAsync(client, stoppingToken),
            AnnounceLoopAsync(client, groupEndpoint, stoppingToken),
            ProbeLoopAsync(client, groupEndpoint, stoppingToken));
    }

    /// <summary>
    /// Every interface this host could be reached on. Multicast is per
    /// interface, and a host with more than one has to be told which — the
    /// operating system otherwise picks by routing metric, and a VPN
    /// adapter routinely wins.
    /// </summary>
    private IReadOnlyList<IPAddress> _interfaces = [];

    /// <summary>
    /// Joins the discovery group on every usable interface, and reports
    /// which. Joining only the default one is why a Hub could see every
    /// node while no node could see the Hub: the receive path happened to
    /// land on the right adapter and the send path did not.
    /// </summary>
    private static IReadOnlyList<IPAddress> JoinOnEveryInterface(Socket socket, ILogger logger)
    {
        var joined = new List<IPAddress>();
        foreach (var local in LocalAddressSelector.EnumerateLocalAddresses())
        {
            try
            {
                socket.SetSocketOption(
                    SocketOptionLevel.IP, SocketOptionName.AddMembership,
                    new MulticastOption(ProtocolSuite.DiscoveryMulticastGroup, local.Address));
                joined.Add(local.Address);
            }
            catch (SocketException ex)
            {
                // An adapter that refuses is one nothing was reachable on
                // anyway; the others still work.
                logger.LogDebug(ex, "Could not join discovery group on {Address}", local.Address);
            }
        }

        if (joined.Count == 0)
        {
            logger.LogWarning("No interface accepted the discovery group; devices will not see this Hub");
        }
        else
        {
            logger.LogInformation("Announcing on {Addresses}", string.Join(", ", joined));
        }
        return joined;
    }

    /// <summary>
    /// Sends to the multicast group once per interface, naming the
    /// interface each time.
    ///
    /// A single send goes wherever the routing table prefers, which on a
    /// machine running a VPN is usually the VPN. Sending on all of them
    /// costs a few hundred bytes every five seconds and removes the whole
    /// question.
    /// </summary>
    private async Task SendToGroupAsync(
        UdpClient client, byte[] payload, IPEndPoint destination, CancellationToken stoppingToken)
    {
        if (_interfaces.Count == 0)
        {
            await TrySendAsync(client, payload, destination, stoppingToken);
            return;
        }

        foreach (var address in _interfaces)
        {
            try
            {
                client.Client.SetSocketOption(
                    SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                    address.GetAddressBytes());
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "Could not select {Address} for multicast", address);
                continue;
            }
            await TrySendAsync(client, payload, destination, stoppingToken);
        }
    }

    /// <summary>
    /// Sends without letting one unreachable destination end a loop. A
    /// device that is offline, rebooting, or gone must not stop the Hub
    /// discovering everything else — and the whole point of these loops is
    /// to keep running while devices come and go.
    /// </summary>
    private async Task<bool> TrySendAsync(
        UdpClient client, byte[] payload, IPEndPoint destination, CancellationToken stoppingToken)
    {
        try
        {
            await client.SendAsync(payload, destination, stoppingToken);
            return true;
        }
        catch (SocketException ex)
        {
            _logger.LogDebug(ex, "Discovery send to {Destination} failed", destination);
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task ReceiveLoopAsync(UdpClient client, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await client.ReceiveAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException ex)
            {
                // Keep listening, but do not spin: if the socket is broken
                // rather than merely interrupted, a bare continue would burn
                // a core reporting the same error forever.
                _logger.LogWarning(ex, "Discovery receive failed");
                await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken);
                continue;
            }

            if (!DiscoveryMessage.TryParse(datagram.Buffer, out var message))
            {
                continue;
            }

            switch (message)
            {
                case DeviceAnnouncement announce when announce.Id != _config.Id:
                    var record = _registry.Upsert(announce, datagram.RemoteEndPoint.Address);
                    _logger.LogDebug("Announce from {Id} ({Name}) at {Address}", record.Id, record.Name, record.Address);
                    break;

                case DiscoveryProbe:
                    await TrySendAsync(
                        client, BuildAnnounce().Serialize(), datagram.RemoteEndPoint, stoppingToken);
                    break;
            }
        }
    }

    /// <summary>
    /// Probes for devices repeatedly rather than only at startup. The
    /// multicast probe finds devices that are new; the unicast probes to
    /// devices already known keep their liveness accurate even when
    /// multicast is being dropped, because both that probe and the reply it
    /// draws are unicast.
    /// </summary>
    private async Task ProbeLoopAsync(UdpClient client, IPEndPoint groupEndpoint, CancellationToken stoppingToken)
    {
        var probe = new DiscoveryProbe { ProtocolVersion = ProtocolSuite.Version }.Serialize();
        using var timer = new PeriodicTimer(ProbeInterval);
        try
        {
            do
            {
                await SendToGroupAsync(client, probe, groupEndpoint, stoppingToken);

                foreach (var device in _registry.Snapshot())
                {
                    if (IPAddress.TryParse(device.Address, out var address))
                    {
                        await TrySendAsync(
                            client, probe, new IPEndPoint(address, ProtocolSuite.DiscoveryPort), stoppingToken);
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task AnnounceLoopAsync(UdpClient client, IPEndPoint groupEndpoint, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(AnnounceInterval);
        try
        {
            do
            {
                await SendToGroupAsync(client, BuildAnnounce().Serialize(), groupEndpoint, stoppingToken);

                // And directly to every device already known.
                //
                // Multicast is how a device is found the first time, and it
                // is the one direction that has never been proven: the Hub
                // hears every node while no node hears the Hub, because
                // outbound multicast follows the routing table and a VPN
                // adapter wins it. Naming the interface fixes that, but the
                // Hub already has a unicast path to each node — it probes
                // and controls them over it — and using the proven path for
                // the announce as well makes a node knowing about the Hub
                // no longer depend on multicast egress at all.
                //
                // The cost is one small datagram per node every five
                // seconds, against a stream of two hundred packets a
                // second.
                foreach (var device in _registry.Snapshot())
                {
                    if (!device.Online || !IPAddress.TryParse(device.Address, out var address))
                    {
                        continue;
                    }

                    // Told, not inferred: the same-subnet address this
                    // device should use, chosen the way an OTA URL is. A
                    // device that reads the source instead would record
                    // whatever rewrote it on the way.
                    var announce = (BuildAnnounce() with
                    {
                        Address = LocalAddressSelector.ForDevice(device.Address),
                    }).Serialize();
                    await TrySendAsync(
                        client, announce,
                        new IPEndPoint(address, ProtocolSuite.DiscoveryPort), stoppingToken);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private DeviceAnnouncement BuildAnnounce() => new()
    {
        ProtocolVersion = ProtocolSuite.Version,
        Id = _config.Id,
        Name = _config.Name,
        Roles = DeviceRole.HubRoles,
        HardwareProfile = "windows-hub",
        FirmwareVersion = HubInfo.Version,
        // Nodes join by asking the Controller, so the Controller has to say
        // where it can be reached. Without this a node assumes the device
        // control port and knocks on a door the Hub does not have.
        ControlPort = ProtocolSuite.DefaultHubPort,
    };
}

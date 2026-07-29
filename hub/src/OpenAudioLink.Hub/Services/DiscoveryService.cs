using System.Net;
using System.Net.Sockets;
using OpenAudioLink.Core.Devices;
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var client = new UdpClient();
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, ProtocolSuite.DiscoveryPort));
        client.JoinMulticastGroup(ProtocolSuite.DiscoveryMulticastGroup);
        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);

        var groupEndpoint = new IPEndPoint(ProtocolSuite.DiscoveryMulticastGroup, ProtocolSuite.DiscoveryPort);
        await client.SendAsync(new DiscoveryProbe { ProtocolVersion = ProtocolSuite.Version }.Serialize(), groupEndpoint, stoppingToken);

        await Task.WhenAll(
            ReceiveLoopAsync(client, stoppingToken),
            AnnounceLoopAsync(client, groupEndpoint, stoppingToken),
            ProbeLoopAsync(client, groupEndpoint, stoppingToken));
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
                _logger.LogWarning(ex, "Discovery receive failed");
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
                    await client.SendAsync(BuildAnnounce().Serialize(), datagram.RemoteEndPoint, stoppingToken);
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
                await client.SendAsync(probe, groupEndpoint, stoppingToken);

                foreach (var device in _registry.Snapshot())
                {
                    if (IPAddress.TryParse(device.Address, out var address))
                    {
                        await client.SendAsync(
                            probe, new IPEndPoint(address, ProtocolSuite.DiscoveryPort), stoppingToken);
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Probe loop stopped");
        }
    }

    private async Task AnnounceLoopAsync(UdpClient client, IPEndPoint groupEndpoint, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(AnnounceInterval);
        try
        {
            do
            {
                await client.SendAsync(BuildAnnounce().Serialize(), groupEndpoint, stoppingToken);
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
        Role = DeviceRole.Hub,
        HardwareProfile = "windows-hub",
        FirmwareVersion = HubInfo.Version,
    };
}

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
            AnnounceLoopAsync(client, groupEndpoint, stoppingToken));
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

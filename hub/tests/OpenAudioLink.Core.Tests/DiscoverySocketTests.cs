using System.Net;
using System.Net.Sockets;
using OpenAudioLink.Hub.Services;
using Xunit;

namespace OpenAudioLink.Core.Tests;

/// <summary>
/// The Hub probes devices by unicast, so it regularly sends to a device
/// that is rebooting — after an update, especially. A rebooting device's IP
/// stack answers with ICMP port-unreachable, and on Windows that makes the
/// *next* operation on the sending UDP socket fail with WSAECONNRESET. The
/// socket used for discovery is the one the Hub hears every other device
/// on, so left unsuppressed one node restarting takes discovery down until
/// the Hub is restarted.
///
/// On Linux the ICMP error is not surfaced this way and these pass either
/// way; the Windows leg of CI is the one that would catch a regression.
/// </summary>
public class DiscoverySocketTests
{
    /// <summary>A loopback port with nothing listening on it.</summary>
    private static IPEndPoint ClosedPort()
    {
        using var probe = new UdpClient(0, AddressFamily.InterNetwork);
        var port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        return new IPEndPoint(IPAddress.Loopback, port);
    }

    [Fact]
    public async Task Sending_to_a_closed_port_does_not_break_later_sends()
    {
        using var sender = new UdpClient(0, AddressFamily.InterNetwork);
        DiscoveryService.SuppressConnectionReset(sender.Client);
        using var listener = new UdpClient(0, AddressFamily.InterNetwork);
        var listenerEndpoint = (IPEndPoint)listener.Client.LocalEndPoint!;
        var closed = ClosedPort();

        // Several times over, with time for the ICMP reply to come back
        // between them — the failure this guards against needs the error to
        // have arrived before the next call.
        for (var i = 0; i < 3; i++)
        {
            await sender.SendAsync(new byte[] { 1 }, closed);
            await Task.Delay(50);
        }

        await sender.SendAsync("still here"u8.ToArray(), listenerEndpoint);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await listener.ReceiveAsync(timeout.Token);
        Assert.Equal("still here"u8.ToArray(), received.Buffer);
    }

    [Fact]
    public async Task Sending_to_a_closed_port_does_not_break_receiving()
    {
        using var receiver = new UdpClient(0, AddressFamily.InterNetwork);
        DiscoveryService.SuppressConnectionReset(receiver.Client);
        var receiverEndpoint = (IPEndPoint)receiver.Client.LocalEndPoint!;
        var closed = ClosedPort();

        for (var i = 0; i < 3; i++)
        {
            await receiver.SendAsync(new byte[] { 1 }, closed);
            await Task.Delay(50);
        }

        using var sender = new UdpClient(0, AddressFamily.InterNetwork);
        await sender.SendAsync("announce"u8.ToArray(), receiverEndpoint);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await receiver.ReceiveAsync(timeout.Token);
        Assert.Equal("announce"u8.ToArray(), received.Buffer);
    }

    /// <summary>Safe to call on any platform; a no-op where it does not apply.</summary>
    [Fact]
    public void Suppressing_connection_reset_never_throws()
    {
        using var client = new UdpClient(0, AddressFamily.InterNetwork);
        DiscoveryService.SuppressConnectionReset(client.Client);
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OpenAudioLink.Core.Audio;

namespace OpenAudioLink.Hub.Services;

public sealed record TestToneStatus(
    bool Running,
    string? Destination,
    int Port,
    string Encoding,
    double FrequencyHz,
    long PacketsSent);

/// <summary>
/// Streams a generated tone as RTP so the audio path can be verified
/// before any capture or receiver hardware exists: point GStreamer,
/// ffplay or an AES67 receiver at it and the wire format is proven.
///
/// Pacing is catch-up based. System timers are not reliable at 5 ms, so
/// the loop sends however many packets the elapsed time says are due
/// rather than assuming one per tick. RTP timestamps come from the frame
/// counter, so a late or bursty send is still correctly timed audio.
/// </summary>
public sealed class TestToneStreamer : IAsyncDisposable
{
    private readonly ILogger<TestToneStreamer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private TestToneStatus _status = new(false, null, 0, "L24", 0, 0);

    public TestToneStreamer(ILogger<TestToneStreamer> logger)
    {
        _logger = logger;
    }

    public TestToneStatus Status => _status;

    public async Task<TestToneStatus> StartAsync(
        IPAddress destination, int port, AudioStreamFormat format, double frequencyHz)
    {
        format.Validate();

        // Built here rather than in the worker so invalid arguments surface
        // to the caller instead of failing silently after a 200 response.
        var tone = new SineToneSource(format, frequencyHz);

        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();

            _cancellation = new CancellationTokenSource();
            _status = new TestToneStatus(true, destination.ToString(), port, format.RtpmapName, frequencyHz, 0);
            _worker = Task.Run(() => RunAsync(destination, port, format, tone, _cancellation.Token));

            _logger.LogInformation("Test tone started: {Frequency} Hz {Encoding} to {Destination}:{Port}",
                frequencyHz, format.RtpmapName, destination, port);
            return _status;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        if (_cancellation is null)
        {
            return;
        }

        await _cancellation.CancelAsync();
        if (_worker is not null)
        {
            try
            {
                await _worker;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cancellation.Dispose();
        _cancellation = null;
        _worker = null;
        _status = _status with { Running = false };
        _logger.LogInformation("Test tone stopped after {Packets} packets", _status.PacketsSent);
    }

    private async Task RunAsync(
        IPAddress destination, int port, AudioStreamFormat format, SineToneSource tone, CancellationToken cancellationToken)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        if (IsMulticast(destination))
        {
            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
        }

        var endpoint = new IPEndPoint(destination, port);
        var packetizer = new RtpAudioPacketizer(format);
        var samples = new float[format.SamplesPerPacket];
        var packet = new byte[format.PacketBytes];

        var clock = Stopwatch.StartNew();
        long packetsSent = 0;
        double packetMs = format.PacketMilliseconds;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                long due = (long)(clock.Elapsed.TotalMilliseconds / packetMs);

                // Cap catch-up so a long stall cannot produce an unbounded burst.
                if (due - packetsSent > 20)
                {
                    packetsSent = due - 20;
                    packetizer.MarkDiscontinuity();
                }

                while (packetsSent < due && !cancellationToken.IsCancellationRequested)
                {
                    tone.ReadFrames(samples);
                    int length = packetizer.WritePacket(samples, packet);
                    await socket.SendAsync(packet.AsMemory(0, length), endpoint, cancellationToken);
                    packetsSent++;
                }

                _status = _status with { PacketsSent = packetsSent };
                await Task.Delay(1, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Test tone stream failed");
            _status = _status with { Running = false };
        }
    }

    private static bool IsMulticast(IPAddress address) =>
        address.GetAddressBytes()[0] is >= 224 and <= 239;

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }
}

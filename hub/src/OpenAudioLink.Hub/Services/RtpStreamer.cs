using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OpenAudioLink.Core.Audio;

namespace OpenAudioLink.Hub.Services;

public sealed record StreamStatus(
    bool Running,
    string? Source,
    string? Description,
    string? Destination,
    int Port,
    string Encoding,
    long PacketsSent,
    long UnderrunSamples);

/// <summary>
/// Sends one RTP audio stream from any <see cref="IAudioSource"/>.
///
/// Pacing is catch-up based. System timers are not reliable at 5 ms, so
/// the loop sends however many packets the elapsed time says are due
/// rather than assuming one per tick. RTP timestamps come from the frame
/// counter, so a late or bursty send is still correctly timed audio.
/// </summary>
public sealed class RtpStreamer : IAsyncDisposable
{
    private readonly ILogger<RtpStreamer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private AudioStreamFormat _format = new();
    private StreamStatus _status = new(false, null, null, null, 0, "L24", 0, 0);

    public RtpStreamer(ILogger<RtpStreamer> logger)
    {
        _logger = logger;
    }

    public StreamStatus Status => _status;

    public AudioStreamFormat Format => _format;

    /// <summary>
    /// Starts streaming, replacing any stream already running. The
    /// streamer takes ownership of <paramref name="source"/> and disposes
    /// it when the stream stops.
    /// </summary>
    public async Task<StreamStatus> StartAsync(
        string sourceKind, IAudioSource source, IPAddress destination, int port, AudioStreamFormat format)
    {
        ArgumentNullException.ThrowIfNull(source);
        format.Validate();

        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();

            _format = format;
            _cancellation = new CancellationTokenSource();
            _status = new StreamStatus(
                true, sourceKind, source.Description, destination.ToString(), port, format.RtpmapName, 0, 0);
            _worker = Task.Run(() => RunAsync(source, destination, port, format, _cancellation.Token));

            _logger.LogInformation("Streaming {Description} as {Encoding} to {Destination}:{Port}",
                source.Description, format.RtpmapName, destination, port);
            return _status;
        }
        catch
        {
            source.Dispose();
            throw;
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
        _logger.LogInformation("Stream stopped after {Packets} packets", _status.PacketsSent);
    }

    private async Task RunAsync(
        IAudioSource source, IPAddress destination, int port, AudioStreamFormat format,
        CancellationToken cancellationToken)
    {
        using var owned = source;
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
                    source.ReadFrames(samples);
                    int length = packetizer.WritePacket(samples, packet);
                    await socket.SendAsync(packet.AsMemory(0, length), endpoint, cancellationToken);
                    packetsSent++;
                }

                _status = _status with
                {
                    PacketsSent = packetsSent,
                    UnderrunSamples = source.UnderrunSamples,
                };
                await Task.Delay(1, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stream failed");
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

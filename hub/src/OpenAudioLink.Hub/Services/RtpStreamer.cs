using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using OpenAudioLink.Core.Audio;

namespace OpenAudioLink.Hub.Services;

public sealed record StreamStatus(
    bool Running,
    string? Source,
    string? Description,
    IReadOnlyList<string> Destinations,
    int Port,
    string Encoding,
    long PacketsSent,
    long UnderrunSamples,
    long SendErrors = 0,
    string? Error = null);

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
    /// <summary>
    /// Most packets a stall may release at once: 100 ms of audio.
    ///
    /// The rule is that this belongs just under the receiver's headroom
    /// above its playout target — a burst that fits in the ring costs
    /// nothing, and one that does not is trimmed at the far end anyway. It
    /// is tempting to make it smaller to be gentle, and that is a mistake:
    /// anything the cap discards is gone for good, so a cap below the
    /// length of a stall converts a burst the receiver could have absorbed
    /// into a gap that no buffer can. Simulated against the observed
    /// sender, a 40 ms cap produced an underrun on every stall while a
    /// 100 ms cap produced almost none.
    /// </summary>
    private const int MaxCatchUpPackets = 20;

    private readonly ILogger<RtpStreamer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private AudioStreamFormat _format = new();
    private StreamStatus _status = new(false, null, null, [], 0, "L24", 0, 0);

    /// <summary>
    /// Where the running stream is going. Swapped whole rather than
    /// mutated, so the send loop can read it without a lock and never sees
    /// a half-built list — the same shape the firmware producer uses for
    /// the same reason.
    /// </summary>
    private volatile IPEndPoint[] _endpoints = [];

    public RtpStreamer(ILogger<RtpStreamer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// The destination list is read back from <see cref="_endpoints"/>
    /// rather than stored in the status record. The send loop rewrites the
    /// status every iteration to publish its counters, so a destination
    /// change written into the same record would be clobbered by the next
    /// tick — the stream would go to the right place while reporting the
    /// wrong one, which is worse than either.
    /// </summary>
    public StreamStatus Status =>
        _status with { Destinations = [.. _endpoints.Select(e => e.Address.ToString())] };

    public AudioStreamFormat Format => _format;

    /// <summary>
    /// Starts streaming, replacing any stream already running. The
    /// streamer takes ownership of <paramref name="source"/> and disposes
    /// it when the stream stops.
    /// </summary>
    public async Task<StreamStatus> StartAsync(
        string sourceKind, IAudioSource source, IReadOnlyList<IPAddress> destinations, int port,
        AudioStreamFormat format)
    {
        ArgumentNullException.ThrowIfNull(source);
        format.Validate();

        if (destinations.Count == 0)
        {
            source.Dispose();
            throw new ArgumentException("At least one destination is required.", nameof(destinations));
        }

        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();

            _format = format;
            _cancellation = new CancellationTokenSource();
            var addresses = destinations.Select(d => d.ToString()).ToList();
            _endpoints = [.. destinations.Select(d => new IPEndPoint(d, port))];
            _status = new StreamStatus(
                true, sourceKind, source.Description, addresses, port, format.RtpmapName, 0, 0);
            // A dedicated thread, not the thread pool. The send loop has a
            // deadline every 5 ms and shares the pool with everything else
            // the Hub does — librespot's pipe reader alone moves 176 KB a
            // second — so its continuations queue behind other work. On
            // hardware that showed up at the far end as the sender falling
            // 80 ms behind and then catching up in a lump, with no packet
            // ever lost. LongRunning gives the loop a thread of its own.
            _worker = Task.Factory.StartNew(
                () => Run(source, format, _cancellation.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            _logger.LogInformation("Streaming {Description} as {Encoding} to {Count} destination(s) {Destinations}:{Port}",
                source.Description, format.RtpmapName, addresses.Count, string.Join(", ", addresses), port);
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

    /// <summary>
    /// Replaces where the running stream is going, without interrupting it.
    ///
    /// This is what lets a speaker that rebooted mid-song rejoin: it asks
    /// the Controller, and the Controller puts it back in the list the
    /// sender is already walking. Restarting the stream instead would make
    /// every other speaker in the room skip.
    /// </summary>
    /// <returns>False when no stream is running.</returns>
    public bool SetDestinations(IReadOnlyList<IPAddress> destinations)
    {
        if (destinations.Count == 0)
        {
            throw new ArgumentException("At least one destination is required.", nameof(destinations));
        }

        if (!_status.Running)
        {
            return false;
        }

        _endpoints = [.. destinations.Select(d => new IPEndPoint(d, _status.Port))];
        _logger.LogInformation("Stream now goes to {Destinations}",
            string.Join(", ", destinations.Select(d => d.ToString())));
        return true;
    }

    /// <summary>
    /// Adds one destination if it is not already there. Idempotent, because
    /// a node that never left still asks to rejoin and must cost nothing by
    /// asking.
    /// </summary>
    /// <returns>False when no stream is running.</returns>
    public bool AddDestination(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (!_status.Running)
        {
            return false;
        }

        var current = _endpoints;
        if (current.Any(e => e.Address.Equals(address)))
        {
            return true;
        }

        _endpoints = [.. current, new IPEndPoint(address, _status.Port)];
        _logger.LogInformation("{Address} joined the running stream", address);
        return true;
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

    /// <summary>
    /// The send loop. Deliberately synchronous: every <c>await</c> inside it
    /// is a chance for the continuation to queue behind other work, and a
    /// loop that must wake 200 times a second cannot afford that. It owns
    /// its thread, so blocking on a send costs nothing but its own time.
    /// </summary>
    private void Run(
        IAudioSource source, AudioStreamFormat format, CancellationToken cancellationToken)
    {
        // Above the Hub's ordinary work, below anything the OS needs. This
        // is the one loop in the process with a hard deadline.
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

        using var owned = source;
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        // Set unconditionally: the destination list can change while the
        // stream runs, so whether a multicast address is in it is not
        // something this line can know once and for all. It costs nothing
        // on a socket that only ever sends unicast.
        socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);

        if (OperatingSystem.IsWindows())
        {
            // Windows fails the *next* operation on a UDP socket with
            // WSAECONNRESET once an ICMP port-unreachable comes back, which
            // happens whenever the receiver is not listening yet, restarts, or
            // closes. Left enabled it kills a live stream for something that is
            // not the sender's fault, so turn it off (SIO_UDP_CONNRESET).
            const int SioUdpConnReset = unchecked((int)0x9800000C);
            try
            {
                socket.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "Could not disable UDP connection reset");
            }
        }

        var packetizer = new RtpAudioPacketizer(format);
        var samples = new float[format.SamplesPerPacket];
        var packet = new byte[format.PacketBytes];

        var clock = Stopwatch.StartNew();
        long packetsSent = 0;
        long sendErrors = 0;
        double packetMs = format.PacketMilliseconds;

        // Without this the loop below sends in clumps; see the type's remarks.
        using var timerResolution = new HighResolutionTimer();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                long due = (long)(clock.Elapsed.TotalMilliseconds / packetMs);

                // Cap catch-up so a long stall cannot produce an unbounded
                // burst.
                if (due - packetsSent > MaxCatchUpPackets)
                {
                    packetsSent = due - MaxCatchUpPackets;
                    packetizer.MarkDiscontinuity();
                }

                while (packetsSent < due && !cancellationToken.IsCancellationRequested)
                {
                    source.ReadFrames(samples);
                    int length = packetizer.WritePacket(samples, packet);

                    // Every destination gets the identical packet — same
                    // SSRC, sequence and timestamp — so receivers applying the
                    // same playout delay stay aligned with each other.
                    //
                    // Read once per packet rather than once per stream: a
                    // speaker that rejoins mid-song is added to this array,
                    // and every destination of one packet must be the same
                    // generation of the list.
                    foreach (var endpoint in _endpoints)
                    {
                        try
                        {
                            socket.Client.SendTo(packet, 0, length, SocketFlags.None, endpoint);
                        }
                        catch (SocketException ex)
                        {
                            // One failed datagram, or one unreachable receiver,
                            // must not end the stream for everyone else.
                            if (sendErrors++ == 0)
                            {
                                _logger.LogWarning(ex, "Send to {Endpoint} failed; continuing", endpoint);
                            }
                        }
                    }

                    packetsSent++;
                }

                _status = _status with
                {
                    PacketsSent = packetsSent,
                    UnderrunSamples = source.UnderrunSamples,
                    SendErrors = sendErrors,
                };
                Thread.Sleep(1);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Surfaced in the status so the UI can say why a stream stopped,
            // instead of the counter simply freezing.
            _logger.LogError(ex, "Stream failed");
            _status = _status with { Running = false, Error = ex.Message };
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }

    /// <summary>
    /// Asks Windows for a 1 ms timer while a stream is running, and gives
    /// it back afterwards.
    ///
    /// The send loop waits with <c>Task.Delay(1)</c>, but Windows' default
    /// timer resolution is 15.6 ms, so each wait is really about that long
    /// and the catch-up loop then releases three packets back to back. The
    /// first hardware test showed what that costs at the far end: the
    /// node's ring overflowed on the clumps and ran dry in the gaps, and
    /// the listener heard the re-buffering several times a second. The
    /// average rate was right the whole time, which is why only the
    /// receiver's counters showed it.
    ///
    /// This is what <c>winmm</c>'s timer API is for and what media
    /// applications have always done. Modern Windows scopes the request to
    /// the process, so it does not slow the rest of the machine down.
    ///
    /// A no-op everywhere else: on Linux and macOS <c>Task.Delay(1)</c>
    /// already resolves near a millisecond.
    /// </summary>
    private sealed class HighResolutionTimer : IDisposable
    {
        private const uint PeriodMs = 1;

        private readonly bool _raised;

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint period);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint period);

        public HighResolutionTimer()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                _raised = TimeBeginPeriod(PeriodMs) == 0;
            }
            catch (DllNotFoundException)
            {
                // Windows without winmm is not a case worth failing a
                // stream over; the pacing is merely coarser.
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        public void Dispose()
        {
            if (!_raised)
            {
                return;
            }

            try
            {
                TimeEndPeriod(PeriodMs);
            }
            catch (DllNotFoundException)
            {
            }
        }
    }
}

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using OpenAudioLink.Core.Audio;
using OpenAudioLink.Core.Devices;
using OpenAudioLink.Core.Net;
using OpenAudioLink.Hub.Configuration;

namespace OpenAudioLink.Hub.Services;

/// <summary>What a recording did, while it runs and after it stops.</summary>
public sealed record RecordingState(
    bool Running, string? File, string? ProducerId, string? ProducerName,
    string? Source, double Seconds, long Packets, long SilenceFrames,
    double SilenceFraction, long Late, long Duplicates, string? Note);

/// <summary>
/// Points a producer node at the Hub and writes what arrives to a WAV file.
/// </summary>
/// <remarks>
/// <para>
/// The Hub does not carry audio (ARCHITECTURE.md section 3) and this is
/// the one deliberate exception. It exists because a measurement has to be
/// kept: a clap, a sweep or a click is worth nothing the moment it has
/// finished playing, and until now the microphone could only ever be
/// listened to.
/// </para>
/// <para>
/// It uses the machinery that was already there rather than inventing a
/// path. A node producer streams to whatever destinations it is given, so
/// the Hub simply names itself as one — the same call the node-to-node
/// link test makes, with a different address in it.
/// </para>
/// <para>
/// <b>Nothing is decoded, resampled or filtered.</b> The payload lands in
/// the file with only the RFC 3190 byte swap applied, because an analysis
/// is worth what the least-processed copy available to it is worth. The
/// arithmetic that matters — what to do about a packet that never came —
/// lives in <see cref="RtpTimeline"/>, which is tested on its own.
/// </para>
/// </remarks>
public sealed class RecordingService : IAsyncDisposable
{
    /// <summary>
    /// The profile's port. The same one the link test uses, because this is
    /// the same kind of stream arriving from the same kind of sender.
    /// </summary>
    public const int Port = 41100;

    /// <summary>
    /// A ceiling, so a recording somebody forgot to stop cannot fill a
    /// disk. Ten minutes of L24 stereo is about 173 MB.
    /// </summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(10);

    private readonly DeviceRegistry _registry;
    private readonly DeviceCommandClient _commands;
    private readonly ILogger<RecordingService> _logger;
    private readonly string _directory;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private UdpClient? _socket;
    private WavWriter? _writer;
    private RtpTimeline? _timeline;
    private CancellationTokenSource? _cancellation;
    private Task? _pump;
    private DeviceRecord? _producer;
    private string? _file;
    private string? _source;
    private string? _note;
    private DateTimeOffset _startedAt;

    public RecordingService(
        DeviceRegistry registry, DeviceCommandClient commands,
        HubPaths paths, ILogger<RecordingService> logger)
    {
        _registry = registry;
        _commands = commands;
        _logger = logger;
        _directory = Path.Combine(paths.DataDirectory, "recordings");

        /*
         * In the constructor, because Program.cs hands this directory to a
         * PhysicalFileProvider while the request pipeline is built and that
         * throws if the root is missing. Creating it when the first
         * recording starts would be too late, and shipped a Hub that would
         * not start once already (0.71.0).
         */
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex, "Could not create the recordings directory {Path}", _directory);
        }
    }

    public string DirectoryPath => _directory;

    public bool Running => _pump is { IsCompleted: false };

    public RecordingState State()
    {
        var w = _writer;
        var t = _timeline;
        return new RecordingState(
            Running, _file, _producer?.Id, _producer?.Name, _source,
            w?.Duration.TotalSeconds ?? 0,
            t?.Written ?? 0, t?.SilenceFrames ?? 0, t?.SilenceFraction ?? 0,
            t?.Late ?? 0, t?.Duplicates ?? 0, _note);
    }

    /// <summary>
    /// Starts <paramref name="producerId"/> streaming to this Hub and
    /// records it.
    /// </summary>
    /// <returns>An error for the operator, or null on success.</returns>
    public async Task<string?> StartAsync(
        string producerId, string source, int toneHz, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Running)
            {
                return "a recording is already running";
            }
            if (!_registry.TryGet(producerId, out var producer))
            {
                return "no such node";
            }
            if (!producer.Online)
            {
                return $"{producer.Name} is offline";
            }

            /*
             * The address the *node* would reach this Hub on, not whichever
             * interface happens to be first. A Hub with a VPN or a second
             * NIC would otherwise name an address the node cannot route to,
             * and the failure is silent: the stream starts, nothing arrives,
             * and the file is empty.
             */
            var local = LocalAddressSelector.ForDevice(producer.Address);
            if (string.IsNullOrWhiteSpace(local))
            {
                return "could not work out which address this node would reach the Hub on";
            }

            var name = $"oal-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Slug(producer.Name)}-{source}.wav";
            var path = Path.Combine(_directory, name);

            _socket = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
            _writer = new WavWriter(File.Create(path), 48000, 2);
            _timeline = new RtpTimeline();
            _producer = producer;
            _file = name;
            _source = source;
            _note = null;
            _startedAt = DateTimeOffset.UtcNow;

            var ok = await _commands.StartStreamAsync(
                producer, [$"{local}:{Port}"], Port, source, toneHz, cancellationToken);
            if (!ok)
            {
                await StopInternalAsync("the node refused to start streaming");
                return "the node refused to start streaming";
            }

            _cancellation = new CancellationTokenSource();
            _pump = Task.Run(() => PumpAsync(_cancellation.Token), CancellationToken.None);

            _logger.LogInformation(
                "Recording {Source} from {Device} to {File}", source, producer.Name, name);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RecordingState> StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_producer is { } producer)
            {
                // Best effort: a node that has gone away must not stop the
                // file being closed properly.
                try
                {
                    await _commands.StopStreamAsync(producer, cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    _logger.LogDebug(ex, "Could not stop the stream on {Device}", producer.Name);
                }
            }
            await StopInternalAsync(_note);
            return State();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopInternalAsync(string? note)
    {
        if (_cancellation is { } cts)
        {
            await cts.CancelAsync();
        }
        if (_pump is { } pump)
        {
            try
            {
                await pump;
            }
            catch (OperationCanceledException)
            {
            }
        }
        _pump = null;
        _cancellation?.Dispose();
        _cancellation = null;
        _socket?.Dispose();
        _socket = null;
        var wrote = _writer is not null;
        _writer?.Dispose();
        _writer = null;
        _note = note;

        if (wrote && _file is not null && _timeline is not null)
        {
            _logger.LogInformation(
                "Recorded {File}: {Packets} packets, {Silence:P2} substituted silence, "
                + "{Late} late, {Duplicates} duplicate",
                _file, _timeline.Written, _timeline.SilenceFraction,
                _timeline.Late, _timeline.Duplicates);
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var socket = _socket!;
        var writer = _writer!;
        var timeline = _timeline!;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(cancellationToken);
                var packet = result.Buffer;

                /*
                 * The profile's header is fixed at 12 bytes with no CSRCs
                 * and no extension, so anything else is not ours: another
                 * application on the port, or a stray probe. Dropped
                 * silently rather than written, because one foreign packet
                 * in the file is a click nobody can explain later.
                 */
                if (packet.Length < 12 + 3
                    || (packet[0] >> 6) != 2
                    || (packet[0] & 0x0F) != 0
                    || (packet[1] & 0x7F) != 96)
                {
                    continue;
                }

                if (!Consume(packet, timeline, writer))
                {
                    continue;
                }

                if (writer.Duration > MaxLength)
                {
                    _note = $"stopped at the {MaxLength.TotalMinutes:0} minute ceiling";
                    _logger.LogInformation("Recording reached its length ceiling; stopping");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _note = ex.Message;
            _logger.LogError(ex, "The recording stopped early");
        }
    }

    /// <summary>
    /// Writes one accepted packet. Separate from the pump because a
    /// <c>Span</c> cannot be a local in an async method, and copying the
    /// payload to satisfy that would allocate 1 440 bytes two hundred
    /// times a second for no reason.
    /// </summary>
    /// <returns>Whether the packet was ours.</returns>
    private static bool Consume(byte[] packet, RtpTimeline timeline, WavWriter writer)
    {
        var payload = packet.AsSpan(12);
        if (payload.Length % (3 * 2) != 0)
        {
            return false;
        }

        var sequence = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));
        var ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8));

        var step = timeline.Accept(sequence, ssrc);
        if (step.SilenceFrames > 0)
        {
            writer.WriteSilence(step.SilenceFrames);
        }
        if (step.Write)
        {
            writer.WriteL24(payload);
        }
        return true;
    }

    /// <summary>A device name reduced to something safe in a filename.</summary>
    private static string Slug(string name)
    {
        var kept = name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray();
        return kept.Length == 0 ? "node" : new string(kept).ToLowerInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        await StopInternalAsync(_note);
        _gate.Dispose();
    }
}

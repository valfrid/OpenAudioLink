using System.Net;
using OpenAudioLink.Core.Audio;
using OpenAudioLink.Core.Devices;

namespace OpenAudioLink.Hub.Services;

/// <summary>Where a room measurement has got to.</summary>
public sealed record RoomMeasurementState(
    bool Running,
    double SecondsElapsed,
    double SecondsPlanned,
    string? SpeakerName,
    string? Channel,
    string? MicrophoneName,
    string? File,
    IReadOnlyList<string> Notes);

/// <summary>
/// One room measurement, start to finish.
/// </summary>
/// <remarks>
/// <para>
/// The three pieces — sweep, recorder, analyser — worked before this
/// existed, and getting a measurement meant driving three panels in the
/// right order without making any of the four or five mistakes that produce
/// a plausible file containing nothing useful. Wrong source on the
/// recorder, sweep to both speakers, microphone still set to line in,
/// stopped before a whole cycle had passed. Every one of those has happened
/// here.
/// </para>
/// <para>
/// So the sequence is written down once, in code, rather than in a
/// procedure somebody has to follow: start the sweep to one speaker, start
/// the microphone recording, run for a whole number of cycles, stop both.
/// The panel keeps its parts visible — they are still useful separately —
/// but the ordinary way to measure a room is one button.
/// </para>
/// <para>
/// <b>Order matters and is fixed here.</b> The sweep starts first, so that
/// nothing but steady state is recorded; the two calls are milliseconds
/// apart, which leaves at most a fraction of a cycle of leading silence —
/// exactly the arbitrary-start case the analyser is tested against.
/// </para>
/// </remarks>
public sealed class RoomMeasurementService : IAsyncDisposable
{
    private readonly DeviceRegistry _registry;
    private readonly RtpStreamer _streamer;
    private readonly RecordingService _recorder;
    private readonly ILogger<RoomMeasurementService> _logger;
    private readonly TimeProvider _time;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _countdown;
    private Task? _timer;
    private DateTimeOffset _startedAt;
    private double _planned;
    private string? _speaker;
    private string? _channel;
    private string? _microphone;
    private string? _file;
    private List<string> _notes = [];

    public RoomMeasurementService(
        DeviceRegistry registry, RtpStreamer streamer, RecordingService recorder,
        ILogger<RoomMeasurementService> logger, TimeProvider? time = null)
    {
        _registry = registry;
        _streamer = streamer;
        _recorder = recorder;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public bool Running => _timer is { IsCompleted: false };

    public RoomMeasurementState State() => new(
        Running,
        Running ? (_time.GetUtcNow() - _startedAt).TotalSeconds : 0,
        _planned, _speaker, _channel, _microphone, _file, _notes);

    /// <returns>An error for the operator, or null on success.</returns>
    public async Task<string?> StartAsync(
        string speaker, string channel, string microphoneId, int cycles,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Running)
            {
                return "a measurement is already running";
            }
            if (_recorder.Running)
            {
                return "a recording is already running; stop it first";
            }

            channel = string.IsNullOrWhiteSpace(channel) ? AudioChannel.Left : channel.Trim();
            if (!AudioChannels.IsKnown(channel))
            {
                return $"'{channel}' is not a channel";
            }

            if (!Resolve(speaker, out var target, out var speakerName))
            {
                return $"'{speaker}' is neither a known speaker nor an address";
            }
            if (!_registry.TryGet(microphoneId, out var microphone))
            {
                return "no such microphone node";
            }
            if (!microphone.Online)
            {
                return $"{microphone.Name} is offline";
            }

            var notes = new List<string>();
            if (microphone.Status?.InputStage is { } stage && stage != "mic")
            {
                // Not refused: a line-level measurement microphone through
                // the ADC is a real arrangement, and the node is the
                // authority on what is plugged into it. But saying nothing
                // here is how an afternoon gets spent measuring a turntable.
                notes.Add(
                    $"{microphone.Name} is set to capture from '{stage}', not its microphone. "
                    + "If a MEMS capsule is what is listening, change its input stage first.");
            }
            if (!AudioChannels.IsHalfOfAPair(channel))
            {
                notes.Add(
                    "The sweep is going to both channels. Two speakers playing it arrive at the "
                    + "microphone at different times, and where they cancel belongs to the pair "
                    + "rather than to either one — measure them one at a time.");
            }

            var format = new AudioStreamFormat();
            var signal = new SweepSignal { SampleRate = format.SampleRate };

            /*
             * The sweep first. What is recorded should be steady state:
             * the room already ringing at the phase it will ring at for
             * every cycle after. The two calls are milliseconds apart, so
             * the leading silence is a fraction of a cycle at worst.
             */
            try
            {
                await _streamer.StartAsync(
                    "sweep", new SweepSource(format, signal, channel), [target],
                    RecordingService.Port, format);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return $"could not start the sweep: {ex.Message}";
            }

            /*
             * The microphone streams to the Hub alone. This is the one
             * measurement where the speakers must NOT be in the list: they
             * are already playing the sweep, and adding the microphone to
             * them puts a microphone and a loudspeaker in one room with a
             * loop between them.
             */
            var error = await _recorder.StartAsync(
                microphone.Id, "capture", 1000, [], cancellationToken);
            if (error is not null)
            {
                await _streamer.StopAsync();
                return error;
            }

            _startedAt = _time.GetUtcNow();
            _planned = signal.TimeToAverage(cycles).TotalSeconds;
            _speaker = speakerName;
            _channel = channel;
            _microphone = microphone.Name;
            _file = _recorder.State().File;
            _notes = notes;

            _countdown = new CancellationTokenSource();
            _timer = RunAsync(_countdown.Token);

            _logger.LogInformation(
                "Measuring {Speaker} ({Channel}) with {Microphone} for {Seconds:0} s into {File}",
                speakerName, channel, microphone.Name, _planned, _file);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops the sweep and the recording. Safe when nothing is running,
    /// which is also what the countdown calls when it expires.
    /// </summary>
    public async Task<RoomMeasurementState> StopAsync(CancellationToken cancellationToken)
    {
        if (_countdown is { } countdown)
        {
            await countdown.CancelAsync();
        }
        if (_timer is { } timer)
        {
            try
            {
                await timer;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await StopBothAsync(cancellationToken);
        return State();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_planned), _time, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // The countdown expiring is the ordinary end of a measurement, so
        // it stops the two streams itself rather than waiting to be asked.
        await StopBothAsync(CancellationToken.None);
        _logger.LogInformation("Measurement finished: {File}", _file);
    }

    private async Task StopBothAsync(CancellationToken cancellationToken)
    {
        _timer = null;
        _countdown?.Dispose();
        _countdown = null;

        try
        {
            await _streamer.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not stop the sweep");
        }

        if (_recorder.Running)
        {
            var state = await _recorder.StopAsync(cancellationToken);
            _file = state.File ?? _file;
        }
    }

    /// <summary>A device id or a literal address, either way an address.</summary>
    private bool Resolve(string speaker, out IPAddress address, out string name)
    {
        address = IPAddress.None;
        name = speaker;

        if (string.IsNullOrWhiteSpace(speaker))
        {
            return false;
        }
        var trimmed = speaker.Trim();

        if (_registry.TryGet(trimmed, out var device))
        {
            name = device.Name;
            return IPAddress.TryParse(device.Address, out address!);
        }
        return IPAddress.TryParse(trimmed, out address!);
    }

    public async ValueTask DisposeAsync()
    {
        if (Running)
        {
            await StopAsync(CancellationToken.None);
        }
        _gate.Dispose();
    }
}

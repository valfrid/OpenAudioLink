using System.Diagnostics;
using OpenAudioLink.Core.Audio;
using OpenAudioLink.Hub.Configuration;

namespace OpenAudioLink.Hub.Services;

/// <summary>
/// One librespot process, standing for one cast point.
///
/// The process runs whether or not anything is playing, because that is
/// what puts "Kitchen" in the phone's device list — docs/CAST-POINTS.md:
/// the receiver instance and the cast point are one object. Streaming
/// starts and stops around it, not the other way round.
///
/// Audio arrives on the process's stdout as raw PCM at 44.1 kHz, is
/// decoded to float, resampled to the stream's rate and left in a ring
/// buffer for the RTP sender to pull from.
/// </summary>
public sealed class LibrespotInstance : IDisposable
{
    /// <summary>
    /// How much audio may sit unread before the reader stops reading.
    ///
    /// This is the whole flow-control mechanism. A pipe backend writes as
    /// fast as it can decode, so a reader that keeps up with it would pull
    /// an entire track into memory in seconds. Stopping at the mark lets
    /// the pipe fill, which blocks librespot, which is exactly the
    /// back-pressure a sound card would have applied. What is held here
    /// also becomes the stream's cushion against scheduling hiccups, so it
    /// is worth having, and it is the latency this adds.
    ///
    /// **Sized against the sender, not against taste.** At 100 ms it was
    /// smaller than the send loop's own worst stall, and the difference is
    /// measurable as silence: when the loop wakes 255 ms late it catches up
    /// by reading 255 ms of audio at once, this cushion holds 100 ms of it,
    /// and the remainder is underrun. An hour of Spotify sent 6.5 seconds
    /// of silence that way, while internet radio — two seconds of ring and
    /// no throttle at all — sent none in six and a half hours, on the same
    /// machine, through the same sender, over the same network.
    ///
    /// Half a second is about double the worst stall measured over that
    /// hour, and half a second of responsiveness is a cheap price on a
    /// source that has nothing to stay in step with. If underruns survive
    /// it, the number is still too small.
    /// </summary>
    private static readonly TimeSpan HighWater = TimeSpan.FromMilliseconds(500);

    /// <summary>Wait before restarting a process that exited.</summary>
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Longer wait after a process that died immediately — a missing
    /// binary, a rejected argument. Retrying those every five seconds fills
    /// the log with the same line forever.
    /// </summary>
    private static readonly TimeSpan FailedStartDelay = TimeSpan.FromSeconds(30);

    private readonly LibrespotOptions _options;
    private readonly ILogger _logger;
    private readonly string _executable;
    private readonly string _cacheDirectory;
    private readonly AudioRingBuffer _buffer;
    private readonly RationalResampler _resampler;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Thread _supervisor;
    private readonly int _highWaterSamples;
    private readonly long _idleMilliseconds;

    private volatile Process? _process;
    private long _lastAudioTicks;
    private long _samplesDelivered;
    private volatile bool _processRunning;
    private volatile string? _error;

    public LibrespotInstance(
        string castPointId, string name, string executable, string cacheDirectory,
        string? zeroconfInterface, LibrespotOptions options, AudioStreamFormat format,
        string? eventScript, int initialVolume, ILogger logger)
    {
        CastPointId = castPointId;
        Name = name;
        ZeroconfInterface = zeroconfInterface;
        EventScript = eventScript;
        InitialVolume = initialVolume;
        _executable = executable;
        _cacheDirectory = cacheDirectory;
        _options = options;
        _logger = logger;

        // Four times the high-water mark, so the throttle has room to work
        // in: a ring only as big as the mark cannot hold what the mark
        // permits, and would wrap while the reader was still deciding to
        // pause.
        _buffer = new AudioRingBuffer(format.SampleRate * format.Channels * 2);
        _resampler = new RationalResampler(
            LibrespotOptions.SourceSampleRate, format.SampleRate, LibrespotOptions.SourceChannels);
        _highWaterSamples = (int)(format.SampleRate * format.Channels * HighWater.TotalSeconds);
        _idleMilliseconds = Math.Max(1, options.IdleSeconds) * 1000L;

        _supervisor = new Thread(Supervise)
        {
            IsBackground = true,
            Name = $"librespot {castPointId}",
        };
        _supervisor.Start();
    }

    /// <summary>The cast point this instance stands for.</summary>
    public string CastPointId { get; }

    /// <summary>What the phone shows. Changing it means a new process.</summary>
    public string Name { get; }

    /// <summary>
    /// The address this instance announces on, or null for librespot's
    /// default of all interfaces. Like the name, it is fixed at spawn, so
    /// changing it means a new process.
    /// </summary>
    public string? ZeroconfInterface { get; }

    /// <summary>
    /// The program librespot runs when something happens, or null to run
    /// none. It is how the phone's volume reaches the speaker.
    /// </summary>
    public string? EventScript { get; }

    /// <summary>
    /// The volume the phone's slider starts at, matched to the speaker's own
    /// so the two agree before anybody touches either.
    /// </summary>
    public int InitialVolume { get; }

    /// <summary>True while the process is up, whether or not it is playing.</summary>
    public bool Running => _processRunning;

    /// <summary>Why the process is not up, when it is not.</summary>
    public string? Error => _error;

    public long SamplesDelivered => Interlocked.Read(ref _samplesDelivered);

    public long DroppedSamples => _buffer.DroppedSamples;

    public long UnderrunSamples => _buffer.UnderrunSamples;

    /// <summary>
    /// Whether audio is flowing, which is what starts and stops the stream.
    ///
    /// Data flow rather than an event hook: librespot's <c>--onevent</c>
    /// would need a helper executable on both platforms to report what the
    /// bytes already say. The second half of the test covers a reader
    /// parked against the high-water mark — it has audio and is being held
    /// back, which is playing, not idle.
    /// </summary>
    public bool Playing =>
        Environment.TickCount64 - Interlocked.Read(ref _lastAudioTicks) < _idleMilliseconds
        || _buffer.Available >= _highWaterSamples;

    /// <summary>
    /// A view onto this instance's audio for the RTP sender. Disposing it
    /// detaches rather than stopping anything: the stream ends, the Spotify
    /// device stays in the phone's list.
    /// </summary>
    public IAudioSource CreateSource() => new InstanceSource(this);

    /// <summary>
    /// Throws away buffered audio. Called when a stream stops, so the next
    /// one does not open with the tail of the last.
    /// </summary>
    public void Discard() => _buffer.Clear();

    private void Supervise()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var started = Environment.TickCount64;
                try
                {
                    RunOnce();
                }
                catch (Exception ex)
                {
                    _error = ex.Message;
                    _logger.LogWarning("librespot for {Name} failed: {Message}", Name, ex.Message);
                }
                finally
                {
                    _processRunning = false;
                }

                if (_stopping.IsCancellationRequested)
                {
                    return;
                }

                // A process that died at once died of something a restart
                // will not fix — a missing binary, a rejected argument —
                // so it waits longer than one that ran and stopped.
                var lived = TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
                var delay = lived < TimeSpan.FromSeconds(2) ? FailedStartDelay : RestartDelay;
                _stopping.Token.WaitHandle.WaitOne(delay);
            }
        }
        catch (ObjectDisposedException)
        {
            // The Hub stopped waiting for this thread and disposed the
            // token underneath it. Nothing left to supervise.
        }
    }

    private void RunOnce()
    {
        var start = new ProcessStartInfo
        {
            FileName = _executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in BuildArguments())
        {
            // ArgumentList rather than a joined string: cast points are
            // named "Living room" and quoting that by hand is a bug waiting
            // for the first space.
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };

        // A lambda rather than a method group: the delegate's parameter
        // types come from the event, so this cannot fall out of step with
        // whatever nullability the framework declares.
        DataReceivedEventHandler diagnostic = (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                // librespot's own log, at debug: it is where a failed login
                // says so, and noise the rest of the time.
                _logger.LogDebug("librespot {Name}: {Line}", Name, args.Data);
            }
        };
        process.ErrorDataReceived += diagnostic;

        process.Start();
        process.BeginErrorReadLine();

        _process = process;
        _processRunning = true;
        _error = null;
        _logger.LogInformation("librespot up for {Name} (pid {Pid})", Name, process.Id);

        try
        {
            Pump(process.StandardOutput.BaseStream);
        }
        finally
        {
            process.ErrorDataReceived -= diagnostic;
            _processRunning = false;
            _process = null;

            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                              or System.ComponentModel.Win32Exception)
                {
                    // Already gone, or gone between the check and the kill.
                }
            }
            process.WaitForExit(5000);

            if (!_stopping.IsCancellationRequested)
            {
                _error = $"librespot exited with code {SafeExitCode(process)}";
                _logger.LogWarning("librespot for {Name} exited ({Code}); restarting",
                    Name, SafeExitCode(process));
            }
        }
    }

    /// <summary>
    /// Reads raw PCM until the pipe closes, converting it to the stream's
    /// rate on the way in.
    /// </summary>
    private void Pump(Stream output)
    {
        var format = _options.SampleFormat;
        int width = PcmDecoder.BytesPerSample(format);
        const int channels = LibrespotOptions.SourceChannels;

        // A restarted process is a new stream of audio; the filter history
        // belongs to the one that died.
        _resampler.Reset();

        var raw = new byte[16 * 1024];
        // Sized for the smallest sample width, so one buffer serves every
        // format rather than being right for the one it was written for.
        var decoded = new float[raw.Length / 2];
        var resampled = new float[
            (_resampler.MaxOutputFrames(decoded.Length / channels) + 1) * channels];

        int carried = 0;
        while (!_stopping.IsCancellationRequested)
        {
            // Flow control: stop reading while there is already enough
            // audio waiting, and let the pipe hold librespot back.
            while (_buffer.Available >= _highWaterSamples && !_stopping.IsCancellationRequested)
            {
                Thread.Sleep(5);
            }
            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            int read = output.Read(raw, carried, raw.Length - carried);
            if (read <= 0)
            {
                return;
            }

            Interlocked.Exchange(ref _lastAudioTicks, Environment.TickCount64);

            // Whole frames only. A partial frame decoded now would put the
            // channels out of step for the rest of the track, which is a
            // swap that never recovers rather than one bad sample.
            int have = carried + read;
            int frames = have / (width * channels);
            int used = frames * width * channels;

            if (frames > 0)
            {
                int samples = PcmDecoder.Decode(raw.AsSpan(0, used), decoded, format);
                int written = _resampler.Process(decoded.AsSpan(0, samples), resampled);
                _buffer.Write(resampled.AsSpan(0, written));
                Interlocked.Add(ref _samplesDelivered, written);
            }

            carried = have - used;
            if (carried > 0)
            {
                Array.Copy(raw, used, raw, 0, carried);
            }
        }
    }

    private IEnumerable<string> BuildArguments()
    {
        yield return "--name";
        yield return Name;
        yield return "--backend";
        yield return "pipe";
        yield return "--format";
        yield return PcmDecoder.ToLibrespotName(_options.SampleFormat);
        yield return "--device-type";
        yield return _options.DeviceType;
        yield return "--bitrate";
        yield return _options.Bitrate.ToString();
        yield return "--initial-volume";
        yield return InitialVolume.ToString();

        /*
         * Hands the samples over untouched, so the only gain in the system
         * is the speaker's — decision 14's rule, which this path has been
         * quietly breaking since it was the first source.
         *
         * "fixed" is librespot's name for a volume control that does not
         * scale anything. Paired with the event script below, which carries
         * the phone's slider to the speaker, so the control still works —
         * it just works in one place instead of two multiplying.
         *
         * Both or neither: without the script this would leave the phone's
         * slider inert, which is a worse fault than the one being fixed.
         */
        if (_options.UnifiedVolume && EventScript is not null)
        {
            yield return "--volume-ctrl";
            yield return "fixed";
            yield return "--onevent";
            yield return EventScript;
        }
        // Credentials live here. Without it every restart of the Hub makes
        // the operator log in from the phone again, which is the kind of
        // small daily friction that decides whether a thing gets used.
        yield return "--cache";
        yield return _cacheDirectory;
        // The audio cache is the part not worth keeping: it is large, it is
        // per cast point, and the audio is going straight out to the air.
        yield return "--disable-audio-cache";

        // Without this librespot binds every interface and lets the
        // operating system choose where multicast goes, which on a machine
        // with a VPN is the wrong interface — see LibrespotOptions.
        if (!string.IsNullOrWhiteSpace(ZeroconfInterface))
        {
            yield return "--zeroconf-interface";
            yield return ZeroconfInterface;
        }

        if (!string.IsNullOrWhiteSpace(_options.ExtraArguments))
        {
            foreach (var extra in _options.ExtraArguments.Split(
                         ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return extra;
            }
        }
    }

    private static string SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode.ToString();
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();

        var process = _process;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                          or System.ComponentModel.Win32Exception)
            {
            }
        }

        // The reader is blocked on a pipe that killing the process closes,
        // so it comes back promptly; the wait is bounded anyway because a
        // Hub shutting down must not hang on a stuck child.
        _supervisor.Join(TimeSpan.FromSeconds(5));
        _stopping.Dispose();
    }

    /// <summary>
    /// Deliberately does not own the instance. The RTP streamer disposes
    /// whatever source it was given when a stream ends, and the process
    /// must survive that.
    /// </summary>
    private sealed class InstanceSource : IAudioSource
    {
        private readonly LibrespotInstance _instance;

        public InstanceSource(LibrespotInstance instance)
        {
            _instance = instance;
        }

        public string Description => $"Spotify Connect: {_instance.Name}";

        public long UnderrunSamples => _instance.UnderrunSamples;

        public void ReadFrames(Span<float> destination) => _instance._buffer.Read(destination);

        public void Dispose()
        {
        }
    }
}

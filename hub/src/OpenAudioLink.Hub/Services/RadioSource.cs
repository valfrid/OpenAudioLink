using NAudio.FileFormats.Mp3;
using NAudio.Wave;
using NAudio.Wave.Compression;
using OpenAudioLink.Core.Audio;
using OpenAudioLink.Core.Radio;

namespace OpenAudioLink.Hub.Services;

/// <summary>
/// An internet radio station as an <see cref="IAudioSource"/>.
/// </summary>
/// <remarks>
/// The cheapest source this project will ever add, because everything
/// downstream already exists: fetch, decode, resample, hand over PCM. No
/// separate process, no account, no sign-in, no zeroconf — see
/// <c>ROADMAP.md</c> for why it was chosen over the alternatives.
///
/// MP3 only for now. That covers SomaFM, Sveriges Radio and most of what
/// people actually listen to, and it decodes frame by frame off a live
/// socket — which matters, because the usual .NET audio readers want a
/// seekable stream and a radio station is the opposite of seekable. AAC
/// and FLAC need either a Media Foundation reader or ffmpeg, and FLAC is
/// the one that makes this the only lossless source available.
/// </remarks>
public sealed class RadioSource : IAudioSource
{
    /// <summary>
    /// Two seconds of buffer. Far more than the playout side's, and
    /// deliberately: a station is on the far side of the internet, where a
    /// stall is measured in seconds rather than the tens of milliseconds a
    /// local sender manages. Latency costs nothing here — nobody is
    /// listening to a turntable in the same room.
    /// </summary>
    private static readonly TimeSpan Buffer = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long to wait before reconnecting, and it does not back off.
    /// A station that dropped is a station somebody wants back, and the
    /// failure is nearly always transient. Backing off would turn a
    /// ten-second outage into a two-minute one.
    /// </summary>
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(3);

    private readonly AudioRingBuffer _buffer;
    private readonly AudioStreamFormat _format;
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Thread _worker;
    private readonly string _url;

    private volatile bool _disposed;

    public RadioSource(string url, AudioStreamFormat format, HttpClient http, ILogger logger)
    {
        _url = url;
        _format = format;
        _http = http;
        _logger = logger;
        _buffer = new AudioRingBuffer(
            (int)(format.SampleRate * format.Channels * Buffer.TotalSeconds));

        Description = $"Internet radio: {url}";

        // A dedicated thread rather than the pool, for the reason the send
        // loop needed one: this reads a socket continuously and would
        // otherwise share a pool with everything else the Hub does.
        _worker = new Thread(Run) { IsBackground = true, Name = "oal-radio" };
        _worker.Start();
    }

    public string Description { get; private set; }

    public long UnderrunSamples => _buffer.UnderrunSamples;

    public void ReadFrames(Span<float> destination) => _buffer.Read(destination);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _cancellation.Cancel();
        _worker.Join(TimeSpan.FromSeconds(2));
        _cancellation.Dispose();
    }

    private void Run()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                Play(_cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Radio {Url} stopped; reconnecting", _url);

                /*
                 * Into the description, because the description is the only
                 * thing about a running stream that reaches a person: it is
                 * what /api/stream returns and what the switchboard shows
                 * while something is playing. Without this, a station that
                 * has moved or gone away is indistinguishable from one
                 * playing quietly — the stream is running, packets are
                 * going out, and they are all silence.
                 */
                Description = $"Internet radio: {_url} — {ex.Message}";
            }

            // Silence rather than a stalled stream while reconnecting. The
            // sender keeps its timing and receivers keep their buffers fed,
            // which is what IAudioSource asks for.
            _cancellation.Token.WaitHandle.WaitOne(Retry);
        }
    }

    private void Play(CancellationToken cancellationToken)
    {
        var stream = Resolve(_url, cancellationToken);

        /*
         * No Icy-MetaData header, so the server does not interleave track
         * titles into the audio. Asking for them means stripping a block
         * every icy-metaint bytes, and getting that arithmetic wrong is
         * indistinguishable from a corrupt stream. Worth adding once the
         * GUI has somewhere to show a title, and not before.
         */
        using var response = _http.Send(
            new HttpRequestMessage(HttpMethod.Get, stream),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var name = response.Headers.TryGetValues("icy-name", out var values)
            ? values.FirstOrDefault()
            : null;
        if (!string.IsNullOrWhiteSpace(name))
        {
            Description = $"Internet radio: {name}";
        }

        _logger.LogInformation("Radio playing {Description} from {Url}", Description, stream);

        using var body = response.Content.ReadAsStream(cancellationToken);
        Decode(body, cancellationToken);
    }

    /// <summary>
    /// Follows a playlist to the stream behind it, once.
    /// </summary>
    /// <remarks>
    /// Once, not repeatedly: a playlist naming a playlist is either a loop
    /// or a mistake, and neither is worth chasing.
    /// </remarks>
    private string Resolve(string url, CancellationToken cancellationToken)
    {
        using var head = _http.Send(
            new HttpRequestMessage(HttpMethod.Get, url),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        head.EnsureSuccessStatusCode();

        var type = head.Content.Headers.ContentType?.ToString();
        if (!StationPlaylist.LooksLikePlaylist(type, url))
        {
            return url;
        }

        var target = StationPlaylist.Parse(head.Content.ReadAsStringAsync(cancellationToken).Result);
        return target.Kind switch
        {
            StationKind.Stream => target.Urls[0],
            StationKind.SegmentPlaylist => throw new NotSupportedException(
                $"{url} is an HLS or DASH playlist. Those are a streaming client rather than "
                + "a stream, and are not supported — see ROADMAP.md."),
            _ => throw new InvalidOperationException($"{url} names no playable stream."),
        };
    }

    /// <summary>
    /// An MP3 decoder that works in this process.
    /// </summary>
    /// <remarks>
    /// DMO first, and this is the whole reason internet radio was silent
    /// from the day it was written.
    ///
    /// <c>AcmMp3FrameDecompressor</c> uses Audio Compression Manager, and
    /// Microsoft's MP3 ACM codec — <c>l3codeca.acm</c> — has only ever
    /// existed as a 32-bit binary. A 64-bit process cannot load it, and the
    /// Hub publishes win-x64 self-contained. So constructing it threw on the
    /// first frame of every station, every time; the worker caught it,
    /// waited three seconds and tried again forever, and the stream ran
    /// perfectly while carrying nothing but silence.
    ///
    /// The DMO decoder ships with Windows in both architectures and is what
    /// NAudio recommends on x64. ACM is kept as a fallback rather than
    /// deleted: it costs one catch, and it is the one that works on a 32-bit
    /// host if this is ever built for one.
    /// </remarks>
    private IMp3FrameDecompressor CreateDecompressor(Mp3WaveFormat format)
    {
        try
        {
            var dmo = new DmoMp3FrameDecompressor(format);
            _logger.LogDebug("Radio decoding through the DMO MP3 decoder");
            return dmo;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No DMO MP3 decoder; falling back to ACM");
        }

        try
        {
            return new AcmMp3FrameDecompressor(format);
        }
        catch (Exception ex)
        {
            // Said plainly, because the symptom is silence and the cause is
            // not something anybody guesses from listening.
            throw new NotSupportedException(
                "This Windows install has no MP3 decoder this Hub can use — neither the DMO "
                + "decoder nor an ACM codec. N and KN editions ship without the media features; "
                + "installing the Media Feature Pack provides them.", ex);
        }
    }

    private void Decode(Stream body, CancellationToken cancellationToken)
    {
        IMp3FrameDecompressor? decompressor = null;
        RationalResampler? resampler = null;

        // Sized for the largest MP3 frame at the highest rate, with room to
        // spare: getting this wrong shows up as clipped audio rather than
        // as an exception, which is the kind of bug that survives testing.
        var pcm = new byte[16384];
        float[]? decoded = null;
        float[]? resampled = null;
        var outputChannels = 2;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = Mp3Frame.LoadFromStream(body);
                if (frame is null)
                {
                    return; // The station closed the connection.
                }

                if (decompressor is null)
                {
                    /*
                     * The station's own rate, taken from its first frame
                     * rather than assumed. Most are 44.1 kHz, some are 48,
                     * and a few are 32 — and decision 13 says the source
                     * resamples, so all three are equally fine here and
                     * none of them reach the wire.
                     */
                    var channels = frame.ChannelMode == ChannelMode.Mono ? 1 : 2;
                    decompressor = CreateDecompressor(new Mp3WaveFormat(
                        frame.SampleRate, channels, frame.FrameLength, frame.BitRate));

                    // What the decoder actually produces, not what the frame
                    // header implies. A decoder is free to hand back stereo
                    // for a mono source, and duplicating channels that were
                    // already duplicated writes twice as many samples as the
                    // buffer expects — which is a speed change, not a click.
                    outputChannels = decompressor.OutputFormat.Channels;

                    if (frame.SampleRate != _format.SampleRate)
                    {
                        resampler = new RationalResampler(
                            frame.SampleRate, _format.SampleRate, _format.Channels);
                    }

                    _logger.LogInformation(
                        "Radio decoding {Rate} Hz {Channels} channel MP3 at {Bitrate} bps{Conversion}",
                        frame.SampleRate, channels, frame.BitRate,
                        resampler is null ? "" : $", resampling to {_format.SampleRate} Hz");
                }

                var bytes = decompressor.DecompressFrame(frame, pcm, 0);
                if (bytes <= 0)
                {
                    continue;
                }

                // 16-bit signed PCM out of the decoder, interleaved.
                var samples = bytes / 2;
                var mono = outputChannels == 1;
                var wanted = mono ? samples * 2 : samples;

                if (decoded is null || decoded.Length < wanted)
                {
                    decoded = new float[wanted];
                }

                for (var i = 0; i < samples; i++)
                {
                    var value = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8)) / 32768f;
                    if (mono)
                    {
                        // Both channels, so a mono station fills a stereo
                        // profile rather than playing out of one speaker.
                        decoded[i * 2] = value;
                        decoded[i * 2 + 1] = value;
                    }
                    else
                    {
                        decoded[i] = value;
                    }
                }

                if (resampler is null)
                {
                    _buffer.Write(decoded.AsSpan(0, wanted));
                    continue;
                }

                var room = resampler.MaxOutputFrames(wanted / _format.Channels) * _format.Channels;
                if (resampled is null || resampled.Length < room)
                {
                    resampled = new float[room];
                }

                var produced = resampler.Process(decoded.AsSpan(0, wanted), resampled);
                _buffer.Write(resampled.AsSpan(0, produced));
            }
        }
        finally
        {
            decompressor?.Dispose();
        }
    }
}

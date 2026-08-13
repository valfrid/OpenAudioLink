using System.Text;
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
/// **MP3 and AAC.** Between them they cover SomaFM, Radio Paradise,
/// Sveriges Radio and most of what people actually listen to.
///
/// The two are decoded differently, for a reason worth knowing before
/// adding a third. MP3 is read frame by frame off the live socket, because
/// the usual .NET audio readers want a *seekable* stream and a radio
/// station is the opposite of seekable — that assumption is what kept this
/// class silent from the day it was written. AAC is handed to Media
/// Foundation as a URL instead, so its own network source deals with the
/// framing and the seeking, and this class never sees the socket.
///
/// FLAC is the one still open, and it is the one that would make this the
/// only lossless source in the project. It usually arrives inside an Ogg
/// container, so it needs a container parser as well as a decoder.
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

    /// <summary>
    /// The most of a playlist worth reading. Past this it is not one, and
    /// reading further is how a stream got mistaken for a file and consumed
    /// forever.
    /// </summary>
    private const int MaxPlaylistBytes = 64 * 1024;

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
                // The type as well as the message. "Specified method is not
                // supported." is NotSupportedException's default text and
                // says nothing about where it came from; the type name is
                // what turned that into a diagnosis.
                Description = $"Internet radio: {_url} — {ex.GetType().Name}: {ex.Message}";
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
        using var response = _http.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, stream),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        var name = response.Headers.TryGetValues("icy-name", out var values)
            ? values.FirstOrDefault()
            : null;
        if (!string.IsNullOrWhiteSpace(name))
        {
            Description = $"Internet radio: {name}";
        }

        _logger.LogInformation("Radio playing {Description} from {Url}", Description, stream);

        using var body = response.Content
            .ReadAsStreamAsync(cancellationToken).GetAwaiter().GetResult();

        /*
         * Which codec, decided from the first bytes rather than the header.
         *
         * A station's Content-Type is frequently wrong — audio/mpeg is
         * served for AAC by more than one broadcaster — and the frame sync
         * at the start of the stream is not. So the bytes decide, and the
         * header only breaks a tie when there is no sync to read.
         */
        var head = new byte[4];
        int peeked = ReadFully(body, head);
        var counted = new CountingStream(body, head.AsSpan(0, peeked).ToArray());

        if (LooksLikeAac(response.Content.Headers.ContentType?.MediaType, head.AsSpan(0, peeked)))
        {
            /*
             * Media Foundation fetches AAC itself.
             *
             * Handing it the URL rather than the socket already open here is
             * deliberate: MF's own network source knows about ADTS framing
             * and ICY responses, and the alternative is feeding frames to a
             * decoder transform by hand — the same work again, for a codec
             * whose framing this project has no reason to learn.
             *
             * The connection opened above is closed by the using; the second
             * one costs a moment at the start of a station that then plays
             * for hours.
             */
            counted.Dispose();
            PlayAac(stream, cancellationToken);
            return;
        }

        // Wrapped, because the decoder asks a live stream where it is.
        using (counted)
        {
            Decode(counted, cancellationToken);
        }
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
        using var head = _http.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, url),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).GetAwaiter().GetResult();
        head.EnsureSuccessStatusCode();

        var type = head.Content.Headers.ContentType?.ToString();
        if (!StationPlaylist.LooksLikePlaylist(type, url))
        {
            return url;
        }

        /*
         * Bounded, because this read had no limit and no timeout.
         *
         * The client is configured with an infinite timeout — right for an
         * endless station, fatal here. Anything that looked like a playlist
         * but was actually a stream would be read forever: no audio, no
         * exception, no log line, the worker thread simply gone. A station
         * stuck exactly like that is indistinguishable from one playing
         * silence, which is what "no sound and nothing in the description"
         * turned out to mean.
         *
         * A playlist is a few hundred bytes. Sixty-four kilobytes of it is
         * already not a playlist, and Parse will say so.
         */
        var buffer = new byte[MaxPlaylistBytes];
        int read;
        using (var body = head.Content
            .ReadAsStreamAsync(cancellationToken).GetAwaiter().GetResult())
        {
            read = body.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }

        var target = StationPlaylist.Parse(Encoding.UTF8.GetString(buffer, 0, read));
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


    /// <summary>Reads as much as the buffer holds, or as much as there is.</summary>
    private static int ReadFully(Stream source, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = source.Read(buffer, total, buffer.Length - total);
            if (read <= 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }

    /// <summary>
    /// Whether this stream is AAC rather than MP3.
    /// </summary>
    /// <remarks>
    /// Both begin with a frame sync of eleven or twelve set bits, and the
    /// two bits after it are what separate them: MPEG audio carries a layer
    /// number there, and ADTS — which AAC uses — is required to leave it
    /// zero. So one byte answers it, and answers it more honestly than the
    /// Content-Type, which several broadcasters get wrong in both
    /// directions.
    ///
    /// The header is consulted only when the stream does not begin with a
    /// sync word at all, which usually means an ID3 tag is in the way.
    /// </remarks>
    private static bool LooksLikeAac(string? contentType, ReadOnlySpan<byte> head)
    {
        if (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xF0) == 0xF0)
        {
            return (head[1] & 0x06) == 0;
        }

        return contentType is not null
            && contentType.Contains("aac", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Plays an AAC station through Media Foundation, which decodes it.
    /// </summary>
    /// <remarks>
    /// Windows has had an AAC decoder since Windows 7, so this needs no
    /// extra component and no extra licence. It is also the reason AAC is
    /// worth adding before FLAC: more stations use it — Sveriges Radio and
    /// most European broadcasters — and it costs a reader rather than a
    /// container parser.
    /// </remarks>
    private void PlayAac(string url, CancellationToken cancellationToken)
    {
        using var reader = new MediaFoundationReader(url);
        var source = reader.WaveFormat;

        if (!TryPcmFormat(source, out var sampleFormat))
        {
            throw new NotSupportedException(
                $"Media Foundation decoded this station to {source.Encoding} "
                + $"{source.BitsPerSample}-bit, which this Hub does not read.");
        }

        Description = $"{Description} — AAC {source.SampleRate} Hz";
        _logger.LogInformation(
            "Radio decoding AAC at {Rate} Hz, {Channels} channel(s), as {Encoding} {Bits}-bit",
            source.SampleRate, source.Channels, source.Encoding, source.BitsPerSample);

        var resampler = source.SampleRate == _format.SampleRate
            ? null
            : new RationalResampler(source.SampleRate, _format.SampleRate, _format.Channels);

        var raw = new byte[16384];
        var width = PcmDecoder.BytesPerSample(sampleFormat);
        float[]? decoded = null;
        float[]? resampled = null;
        int idleReads = 0;

        /*
         * Stop reading while there is already enough audio waiting.
         *
         * The MP3 path needs no throttle because the station itself paces
         * it — bytes arrive in real time. Media Foundation buffers ahead and
         * hands over as fast as it is asked, so this loop can outrun real
         * time, fill a two-second ring, and start overwriting audio that has
         * not been sent yet. The ring drops the oldest samples when it
         * overflows, which is heard as skipping rather than as a gap.
         *
         * This is the same flow control LibrespotInstance applies to a pipe,
         * and for the same reason: whatever is upstream has to be told to
         * wait, and there is no other way to tell it.
         */
        int highWater = _format.SampleRate * _format.Channels * 3 / 2;

        while (!cancellationToken.IsCancellationRequested)
        {
            while (_buffer.Available >= highWater && !cancellationToken.IsCancellationRequested)
            {
                Thread.Sleep(5);
            }

            int read = reader.Read(raw, 0, raw.Length);
            if (read <= 0)
            {
                /*
                 * Nothing right now is not the same as nothing ever.
                 *
                 * A file reader returns zero at the end of the file, and this
                 * loop treated that as the station hanging up: it returned,
                 * the worker waited three seconds and reconnected. On a live
                 * stream that produced two seconds of music and four of
                 * silence, over and over, with not a single packet lost —
                 * the sender was faithfully sending an empty ring.
                 *
                 * A live reader can simply be between buffers. So wait a
                 * little and ask again, and only treat a second of continuous
                 * nothing as the end.
                 */
                if (++idleReads > 50)
                {
                    return;
                }
                Thread.Sleep(20);
                continue;
            }

            idleReads = 0;

            // Whole samples only; a partial one carried into the next read
            // would put the channels out of step for good.
            read -= read % width;
            if (read == 0)
            {
                continue;
            }

            int samples = read / width;
            bool mono = source.Channels == 1;
            int wanted = mono ? samples * 2 : samples;

            if (decoded is null || decoded.Length < wanted)
            {
                decoded = new float[wanted];
            }

            if (mono)
            {
                // Into every other slot first, then duplicated, so a mono
                // station fills a stereo profile rather than playing out of
                // one speaker.
                var scratch = new float[samples];
                PcmDecoder.Decode(raw.AsSpan(0, read), scratch, sampleFormat);
                for (int i = 0; i < samples; i++)
                {
                    decoded[i * 2] = scratch[i];
                    decoded[i * 2 + 1] = scratch[i];
                }
            }
            else
            {
                PcmDecoder.Decode(raw.AsSpan(0, read), decoded, sampleFormat);
            }

            if (resampler is null)
            {
                _buffer.Write(decoded.AsSpan(0, wanted));
                continue;
            }

            int room = resampler.MaxOutputFrames(wanted / _format.Channels) * _format.Channels;
            if (resampled is null || resampled.Length < room)
            {
                resampled = new float[room];
            }

            int produced = resampler.Process(decoded.AsSpan(0, wanted), resampled);
            _buffer.Write(resampled.AsSpan(0, produced));
        }
    }

    /// <summary>What Media Foundation handed back, as a format this reads.</summary>
    private static bool TryPcmFormat(WaveFormat format, out PcmSampleFormat sampleFormat)
    {
        sampleFormat = PcmSampleFormat.S16;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            sampleFormat = PcmSampleFormat.F32;
            return true;
        }
        if (format.Encoding != WaveFormatEncoding.Pcm)
        {
            return false;
        }
        switch (format.BitsPerSample)
        {
            case 16: sampleFormat = PcmSampleFormat.S16; return true;
            case 24: sampleFormat = PcmSampleFormat.S24_3; return true;
            case 32: sampleFormat = PcmSampleFormat.S32; return true;
            default: return false;
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
        long frames = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = Mp3Frame.LoadFromStream(body);
                if (frame is null)
                {
                    /*
                     * No frame at all means this was never MP3 — an HTML
                     * error page, an AAC or FLAC stream, a playlist that
                     * resolved to the wrong thing. Said out loud, because
                     * returning quietly here is a three-second retry loop
                     * that runs all evening reporting nothing: the stream
                     * runs, packets flow, and every one is silence.
                     *
                     * After the first frame it means the station closed the
                     * connection, which is ordinary and worth no more than
                     * a reconnect.
                     */
                    if (frames == 0)
                    {
                        throw new NotSupportedException(
                            "no MP3 frames in this stream — it is not MP3, or the playlist "
                            + "pointed somewhere else. AAC and FLAC are not decoded yet.");
                    }
                    return;
                }

                frames++;

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

    /// <summary>
    /// A read-only stream that knows how far it has got.
    /// </summary>
    /// <remarks>
    /// This is what made internet radio silent from the day it was written,
    /// on every station, at the first frame.
    ///
    /// <c>Mp3Frame.LoadFromStream</c> opens with
    /// <c>frame.FileOffset = input.Position</c> — before any
    /// <c>CanSeek</c> check — and an HTTP response body cannot answer that.
    /// A non-seekable stream throws <c>NotSupportedException</c> from the
    /// <c>Position</c> getter, whose default message is "Specified method is
    /// not supported.": no mention of position, of seeking, or of the
    /// decoder. Read from the outside it looked like an unsupported audio
    /// format, which cost several wrong diagnoses.
    ///
    /// Counting bytes as they pass answers the question honestly. CanSeek
    /// stays false, which is true and which makes the decoder skip its
    /// seek-based frame lookahead; the position setter moves forward only,
    /// by reading and discarding, because a live stream cannot rewind.
    /// </remarks>
    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        private readonly byte[] _prefix;
        private int _prefixAt;
        private long _position;

        /// <param name="prefix">
        /// Bytes already taken from <paramref name="inner"/> to identify the
        /// codec, served back before anything else. Sniffing a stream is
        /// only free if what was sniffed is not thrown away.
        /// </param>
        public CountingStream(Stream inner, byte[]? prefix = null)
        {
            _inner = inner;
            _prefix = prefix ?? [];
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set
            {
                if (value < _position)
                {
                    throw new NotSupportedException("A live stream cannot be rewound.");
                }
                Skip(value - _position);
            }
        }

        /// <summary>
        /// Reads until the buffer is full or the station really has ended.
        /// </summary>
        /// <remarks>
        /// A Stream may return fewer bytes than asked for, and a network
        /// stream routinely does — one read stops at whatever the last TCP
        /// segment carried. A file almost never does, which is why code
        /// written against files gets away with assuming otherwise.
        ///
        /// Mp3Frame.LoadFromStream assumes otherwise:
        ///
        ///     bytesRead = input.Read(frame.RawData, 4, bytesRequired);
        ///     if (bytesRead &lt; bytesRequired) throw new EndOfStreamException(
        ///         "Unexpected end of stream before frame complete");
        ///
        /// So any frame that happened to straddle a segment boundary — most
        /// of them — read as a truncated file. The station was fine; the
        /// reading of it was not. Looping here gives the decoder the
        /// file-like stream it expects, and is the whole difference between
        /// noise every few seconds and music.
        /// </remarks>
        public override int Read(byte[] buffer, int offset, int count)
        {
            int total = 0;

            while (_prefixAt < _prefix.Length && total < count)
            {
                buffer[offset + total++] = _prefix[_prefixAt++];
            }

            while (total < count)
            {
                int read = _inner.Read(buffer, offset + total, count - total);
                if (read <= 0)
                {
                    break;
                }
                total += read;
            }

            _position += total;
            return total;
        }

        /// <summary>Consumes bytes, since seeking forward is not possible.</summary>
        private void Skip(long count)
        {
            var scratch = new byte[8192];
            while (count > 0)
            {
                int read = Read(scratch, 0, (int)Math.Min(scratch.Length, count));
                if (read <= 0)
                {
                    throw new EndOfStreamException("The station ended before the frame did.");
                }
                count -= read;
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        // The response stream is owned by the caller's using statement.
        protected override void Dispose(bool disposing) => base.Dispose(disposing);
    }

}

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
/// **MP3, AAC and FLAC.** With FLAC this became the only lossless path in
/// the project: the transport has always been uncompressed L24, so the
/// source was the lossy link, and now it need not be.
///
/// Three decoders, and which one a station gets is the whole design.
///
/// **MP3** is read frame by frame off the live socket, because the usual
/// .NET audio readers want a *seekable* stream and a radio station is the
/// opposite of seekable — that assumption is what kept this class silent
/// from the day it was written.
///
/// **FLAC**, in either container, goes to libFLAC on that same socket.
/// Windows decodes FLAC only in its own container and ships no Ogg
/// demuxer, and the FLAC internet radio serves is Ogg-FLAC, so Media
/// Foundation cannot play these stations at all — and does not say so,
/// it just never returns from the open.
///
/// **AAC and anything unrecognised** are handed to Media Foundation as a
/// URL, so its own network source deals with framing and seeking and this
/// class never touches the socket.
///
/// The lesson behind that split, which cost a week: a decoder written for
/// files assumes things a live stream cannot provide. Position. That one
/// read fills the buffer. That end-of-file means end-of-stream. All three
/// were met here in turn, and the next codec will meet them again.
///
/// The fourth was the mirror of the third, and cost a station two silent
/// evenings: because a live stream never ends, the MP3 reader can never
/// reach the point where it would say "there are no frames here". Point it
/// at something that is not MP3 and it searches for a sync until the music
/// stops — no exception, no log line, nothing but a ring that stays empty.
///
/// So only a stream carrying a valid MPEG frame header goes to it, and the
/// stream itself enforces a deadline for producing one. Everything else,
/// recognised or not, goes to Media Foundation. That last part is the
/// correction that mattered: an unidentifiable stream was briefly refused
/// outright, which turned "I cannot tell" into "this will not play". Some
/// stations genuinely cannot be identified from a peek — join an Icecast
/// FLAC stream mid-broadcast and its header went out hours ago — and a
/// real demuxer given the URL knows more than sixty-four bytes ever will.
/// </remarks>
public sealed class RadioSource : IAudioSource
{
    /// <summary>
    /// How much decoded audio to keep ahead of the sender: five seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Far more than the playout side's, and deliberately: a station is on
    /// the far side of the internet, where a stall is measured in seconds
    /// rather than the tens of milliseconds a local sender manages.
    /// </para>
    /// <para>
    /// Latency is the whole cost, and it is paid in full — five seconds
    /// between the station and the speakers, and the same again before a
    /// change of station is heard. That is free for radio, which is already
    /// well behind live and which nobody is playing along to. It would be
    /// unusable for <see cref="SystemAudioSource"/> or librespot, where the
    /// listener is in the room with the source, which is why this constant
    /// lives here and not somewhere shared.
    /// </para>
    /// <para>
    /// What it buys is protection against the source running dry, and
    /// nothing else. It cannot help a sender whose loop is not running: a
    /// dry source still sends packets, on time, carrying silence, so the
    /// receiver's counters stay clean, while a stalled send loop sends
    /// nothing at all and the receiver records an arrival gap. Those are
    /// the two failures this project keeps confusing, and only the first
    /// one is a buffering problem.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan Charge = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The ring itself, a second larger than <see cref="Charge"/>.
    /// </summary>
    /// <remarks>
    /// The producer checks the high-water mark between writes, not within
    /// one, so it can overshoot by whatever a single decoded block holds.
    /// The slack absorbs that; without it the overshoot laps the ring and
    /// overwrites audio that has not been sent, which is heard as skipping.
    /// </remarks>
    private static readonly TimeSpan Ring = Charge + TimeSpan.FromSeconds(1);

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

    /// <summary>How far to look for a first MP3 frame before giving up.</summary>
    private const int MaxSyncSearchBytes = 1024 * 1024;

    /// <summary>
    /// How long Media Foundation gets to open a station before this gives up
    /// on it. Generous, because it is fetching from the far side of the
    /// internet; finite, because it has no obligation to return at all.
    /// </summary>
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(20);

    private readonly AudioRingBuffer _buffer;
    private readonly AudioStreamFormat _format;
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Thread _worker;
    private readonly string _url;

    private volatile bool _disposed;

    /// <summary>
    /// Stop reading while this much audio is already waiting.
    /// </summary>
    /// <remarks>
    /// The MP3 path needs no throttle because the station itself paces it —
    /// bytes arrive in real time. A decoder that buffers ahead hands over as
    /// fast as it is asked, so it can outrun real time, fill the ring and
    /// start overwriting audio that has not been sent yet. That is heard as
    /// skipping rather than as a gap, which makes it easy to misread.
    /// </remarks>
    private int HighWater => Samples(Charge);

    /// <summary>Interleaved samples in the given span of time.</summary>
    private int Samples(TimeSpan span) =>
        (int)(_format.SampleRate * _format.Channels * span.TotalSeconds);

    /// <summary>The station's own name, once it has given one.</summary>
    private string _station;

    public RadioSource(string url, AudioStreamFormat format, HttpClient http, ILogger logger)
    {
        _url = url;
        _format = format;
        _http = http;
        _logger = logger;
        _buffer = new AudioRingBuffer(Samples(Ring));

        _station = $"Internet radio: {url}";
        Description = _station;

        // A dedicated thread rather than the pool, for the reason the send
        // loop needed one: this reads a socket continuously and would
        // otherwise share a pool with everything else the Hub does.
        _worker = new Thread(Run) { IsBackground = true, Name = "oal-radio" };
        _worker.Start();
    }

    public string Description { get; private set; }

    public long UnderrunSamples => _buffer.UnderrunSamples;

    public long BufferedSamples => _buffer.Available;

    public long TargetBufferedSamples => HighWater;

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

    /// <summary>
    /// Where this station has got to, written where a person can see it.
    /// </summary>
    /// <remarks>
    /// Because "no sound" has three times now meant "blocked on a call that
    /// never returned", and each time the description was the last thing set
    /// before the blockage rather than a report of it. A stage marker turns
    /// "stuck somewhere in here" into "stuck exactly here" without anybody
    /// having to reason about which line runs next.
    /// </remarks>
    private void Stage(string what) => Description = $"{_station} — {what}";


    private void Play(CancellationToken cancellationToken)
    {
        _station = $"Internet radio: {_url}";
        Stage("connecting");

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
            _station = $"Internet radio: {name}";
        }

        /*
         * Where the station actually turned out to be, after redirects.
         *
         * HttpClient follows them silently, so everything above — the
         * icy-name, the first bytes, the codec — describes the final URL
         * while the variable still holds the first one. Handing that first
         * one to Media Foundation makes MF repeat the redirect itself, and
         * it is not obliged to be as good at it: a plain http:// address
         * that answers 301 to https:// is a different job for its network
         * source than for HttpClient, and one it can sit on rather than
         * refuse.
         *
         * The station that would not play redirects across schemes. The ones
         * that play do not. That is not proof, but handing over the address
         * the audio really came from is right whether or not it is the
         * cause — the alternative is asking a second client to rediscover
         * something already known.
         */
        var resolved = response.RequestMessage?.RequestUri?.ToString() ?? stream;
        if (!string.Equals(resolved, stream, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Radio {Url} redirected to {Resolved}", stream, resolved);
        }

        _logger.LogInformation("Radio playing {Station} from {Url}", _station, resolved);

        using var body = response.Content
            .ReadAsStreamAsync(cancellationToken).GetAwaiter().GetResult();

        // Reading the first bytes can block on a station that connects and
        // then says nothing, which is its own diagnosis if it is visible.
        Stage("reading the first bytes");

        /*
         * Which codec, decided from the first bytes rather than the header.
         *
         * A station's Content-Type is frequently wrong — audio/mpeg is
         * served for AAC by more than one broadcaster — and the frame sync
         * at the start of the stream is not. So the bytes decide, and the
         * header only breaks a tie when there is no sync to read.
         */
        var head = new byte[StationCodec.SniffBytes];
        int peeked = ReadFully(body, head);
        var counted = new CountingStream(body, head.AsSpan(0, peeked).ToArray());

        var contentType = response.Content.Headers.ContentType?.MediaType;
        var codec = StationCodec.MediaFoundation(contentType, head.AsSpan(0, peeked));

        /*
         * Unrecognised goes to Media Foundation too, rather than being
         * refused, and the difference matters more than it looks.
         *
         * Some stations cannot be identified from a peek at all. Join an
         * Icecast FLAC stream mid-broadcast and the "fLaC" header went out
         * hours ago; if the Content-Type is unhelpful as well, there is
         * genuinely nothing here to read. Refusing on that basis turns "I
         * cannot tell" into "this will not play", which is a worse answer
         * than the one a real demuxer can give — Media Foundation fetches
         * the stream itself, with its own container handling, and either
         * decodes it or fails with a reason.
         *
         * So the only thing that stays on the MP3 path is a stream carrying
         * a valid MPEG frame header. Everything else, known or not, goes to
         * the decoder that can say no.
         */
        if (codec is null && !StationCodec.LooksLikeMp3(head.AsSpan(0, peeked)))
        {
            codec = StationCodec.FromUrl(resolved) ?? "unrecognised";
            _logger.LogInformation(
                "Radio cannot identify {Url} from its first bytes ({Head}, served as {Type}); "
                + "handing it to Media Foundation as {Codec}",
                resolved, StationCodec.Describe(head.AsSpan(0, peeked)),
                contentType ?? "no content-type", codec);
        }

        /*
         * FLAC and Ogg go to libFLAC, on the socket already open.
         *
         * Not to Media Foundation, which cannot play either of them here.
         * Windows decodes FLAC in its own container and ships no Ogg
         * demuxer at all, and the FLAC internet radio actually serves is
         * Ogg-FLAC — Icecast encapsulates it that way. Asked for it anyway,
         * MF does not refuse: it sits on the open until the deadline above
         * fires, which is what six releases of silence looked like.
         *
         * Ogg is passed as Ogg even when the sniff called it FLAC. The
         * Ogg-FLAC mapping embeds the native "fLaC" signature a few bytes
         * into the first page, so a search of the window finds it and gets
         * the container exactly backwards; the container is what decides
         * which entry point libFLAC needs, so it is read again here from
         * the front of the stream where it cannot be confused.
         */
        if (codec is "FLAC" or "Ogg")
        {
            bool ogg = head.AsSpan(0, peeked).StartsWith("OggS"u8);

            /*
             * Said now, by name, rather than as a decode failure later.
             *
             * Ogg carries several codecs and libFLAC reads one of them. A
             * Vorbis stream reaches the decoder, produces nothing, and
             * eventually reports something about an unparseable stream —
             * accurate, and it reads like a fault in the Hub rather than
             * like the wrong entry in a station's format list. Radio
             * Paradise offers Vorbis and FLAC from the same menu, so this
             * is a button somebody will actually press by mistake.
             */
            var inside = StationCodec.OggPayload(head.AsSpan(0, peeked));
            if (inside is not null and not "FLAC")
            {
                counted.Dispose();
                throw new NotSupportedException(
                    $"this station is Ogg {inside}, and only FLAC inside Ogg is decoded. "
                    + "Most stations offering it also publish FLAC, which is lossless and "
                    + "plays here.");
            }

            Stage($"decoding {(ogg ? "Ogg FLAC" : "FLAC")}");
            using (counted)
            {
                DecodeFlac(counted, ogg, cancellationToken);
            }
            return;
        }

        if (codec is not null)
        {
            /*
             * Media Foundation fetches the stream itself.
             *
             * Handing it the URL rather than the socket already open here is
             * deliberate: MF's own network source knows about ADTS framing,
             * Ogg pages and ICY responses, and the alternative is feeding
             * frames to a decoder transform by hand — the same work again,
             * for containers this project has no reason to learn.
             *
             * The connection opened above is closed by the using; the second
             * one costs a moment at the start of a station that then plays
             * for hours.
             */
            counted.Dispose();
            PlayThroughMediaFoundation(resolved, codec, cancellationToken);
            return;
        }

        /*
         * A deadline for the first frame, enforced by the stream rather than
         * by the decoder.
         *
         * The obvious place for this check is the decode loop, and that is
         * where it was, and it never fired: Mp3Frame.LoadFromStream does its
         * scanning inside a single call, so a loop that checks between calls
         * checks nothing. Whatever the reader does with bytes it cannot
         * parse, it has to keep asking for more — so the limit belongs on
         * the hand that feeds it.
         *
         * It is lifted the moment a real frame is decoded, because from then
         * on the only thing that matters is the station staying up.
         */
        counted.SyncSearchLimit = MaxSyncSearchBytes;
        Stage("decoding MP3");

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
    /// Plays a station Media Foundation can decode: AAC, FLAC, or Ogg.
    /// </summary>
    /// <remarks>
    /// Windows has had an AAC decoder since 7 and a FLAC one since 10, so
    /// none of this needs an extra component or an extra licence.
    ///
    /// FLAC cost almost nothing to add once AAC worked, and that ordering
    /// was the point: AAC established that Media Foundation's network
    /// source copes with an endless HTTP stream, which is the assumption
    /// both rest on and the one worth testing with the cheaper of the two.
    /// Adding FLAC was then a signature in the sniff and a rename here.
    ///
    /// Ogg is the one that may still refuse. Media Foundation reads FLAC in
    /// its own container natively; Ogg-wrapped FLAC and Vorbis depend on
    /// what the machine has installed. If it refuses, it says so with a
    /// type name, which is enough to act on.
    /// </remarks>
    private void PlayThroughMediaFoundation(
        string url, string codec, CancellationToken cancellationToken)
    {
        Stage($"opening with Media Foundation as {codec}");

        using var reader = Open(url, codec);
        var source = reader.WaveFormat;

        if (!TryPcmFormat(source, out var sampleFormat))
        {
            throw new NotSupportedException(
                $"Media Foundation decoded this station to {source.Encoding} "
                + $"{source.BitsPerSample}-bit, which this Hub does not read.");
        }

        /*
         * The station's own name, kept aside before anything is appended to
         * it. Appending to Description in place read fine the first time and
         * grew a new "— FLAC 44100 Hz" on every reconnect after that.
         */
        Description = $"{_station} — {codec} {source.SampleRate} Hz";
        _logger.LogInformation(
            "Radio decoding {Codec} at {Rate} Hz, {Channels} channel(s), as {Encoding} {Bits}-bit",
            codec, source.SampleRate, source.Channels, source.Encoding, source.BitsPerSample);

        var resampler = source.SampleRate == _format.SampleRate
            ? null
            : new RationalResampler(source.SampleRate, _format.SampleRate, _format.Channels);

        var raw = new byte[16384];
        var width = PcmDecoder.BytesPerSample(sampleFormat);
        float[]? decoded = null;
        float[]? resampled = null;
        int idleReads = 0;

        /*
         * How much came out of the decoder, and how loud it was.
         *
         * "The link is up, no packets lost, and no sound" has now been three
         * different faults, and from the outside all three looked identical:
         * the sender runs, the node receives, and every sample is zero. The
         * stream description could not tell them apart, so each one cost a
         * round of guessing.
         *
         * These two numbers separate them at a glance. No seconds at all
         * means the decoder is not producing — the format was refused, or
         * the reader is handing back nothing. Seconds climbing with a peak
         * of −∞ means it is producing silence, which is a different fault in
         * a different place. Seconds climbing with a real peak means the
         * decode is fine and the problem is downstream of here.
         *
         * Cheap enough to leave on: one comparison per sample on a thread
         * that is already touching every one of them.
         */
        long framesDecoded = 0;
        float peak = 0f;
        long lastReportMs = Environment.TickCount64;
        bool announced = false;

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
        while (!cancellationToken.IsCancellationRequested)
        {
            while (_buffer.Available >= HighWater && !cancellationToken.IsCancellationRequested)
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
                    /*
                     * Loudly, because returning quietly here is invisible.
                     *
                     * Play() returns without throwing, so Run() logs nothing,
                     * changes nothing, waits three seconds and reconnects —
                     * forever. From outside, a station that never produced a
                     * sample and one that is playing quietly look the same:
                     * the stream runs, packets flow, no loss is reported.
                     *
                     * Thrown rather than logged so it reaches the description,
                     * which is the one thing about a running stream that a
                     * person can see.
                     */
                    throw new EndOfStreamException(
                        framesDecoded == 0
                            ? $"Media Foundation opened this station as {codec} but handed back "
                                + "no audio at all within a second. The format was accepted and "
                                + "the decoder produced nothing."
                            : "the station stopped sending");
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

            for (int i = 0; i < wanted; i++)
            {
                float magnitude = Math.Abs(decoded[i]);
                if (magnitude > peak)
                {
                    peak = magnitude;
                }
            }

            framesDecoded += wanted / _format.Channels;

            if (!announced)
            {
                announced = true;
                _logger.LogInformation(
                    "Radio {Codec} produced its first samples", codec);
            }

            long nowMs = Environment.TickCount64;
            if (nowMs - lastReportMs >= 2000)
            {
                lastReportMs = nowMs;
                Description =
                    $"{_station} — {codec} {source.SampleRate} Hz, "
                    + $"{framesDecoded / source.SampleRate} s, peak {Dbfs(peak)}";

                // Reset, so the figure is the last couple of seconds rather
                // than the loudest moment since the station started. A peak
                // that never falls cannot show a stream going quiet.
                peak = 0f;
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

    /// <summary>
    /// Plays a FLAC station, in either container, through libFLAC.
    /// </summary>
    /// <remarks>
    /// The simplest decode path in this class, and the reason is worth
    /// noting after everything the other two needed: libFLAC was given no
    /// seek callback, so it never asks where it is, never asks how long the
    /// station is, and never decides the stream has ended because a read
    /// came up short. Every workaround the MP3 and Media Foundation paths
    /// carry exists to fake one of those answers.
    ///
    /// The resampler is built on the first frame rather than up front,
    /// because the sample rate arrives with the audio.
    /// </remarks>
    private void DecodeFlac(Stream body, bool ogg, CancellationToken cancellationToken)
    {
        RationalResampler? resampler = null;
        float[]? resampled = null;
        long framesDecoded = 0;
        float peak = 0f;
        long lastReportMs = Environment.TickCount64;
        int sourceRate = 0;
        var label = ogg ? "Ogg FLAC" : "FLAC";

        using var decoder = new FlacStreamDecoder(body, ogg, (samples, count, rate, channels) =>
        {
            if (sourceRate != rate)
            {
                // First frame, or a station that changed underneath us.
                sourceRate = rate;
                resampler = rate == _format.SampleRate
                    ? null
                    : new RationalResampler(rate, _format.SampleRate, _format.Channels);

                _logger.LogInformation(
                    "Radio decoding {Label} at {Rate} Hz, {Channels} channel(s){Conversion}",
                    label, rate, channels,
                    resampler is null ? "" : $", resampling to {_format.SampleRate} Hz");
            }

            for (int i = 0; i < count; i++)
            {
                float magnitude = Math.Abs(samples[i]);
                if (magnitude > peak)
                {
                    peak = magnitude;
                }
            }

            framesDecoded += count / _format.Channels;

            var rate48 = resampler;
            if (rate48 is null)
            {
                _buffer.Write(samples.AsSpan(0, count));
            }
            else
            {
                int room = rate48.MaxOutputFrames(count / _format.Channels) * _format.Channels;
                if (resampled is null || resampled.Length < room)
                {
                    resampled = new float[room];
                }

                int produced = rate48.Process(samples.AsSpan(0, count), resampled);
                _buffer.Write(resampled.AsSpan(0, produced));
            }
        });

        while (!cancellationToken.IsCancellationRequested)
        {
            // The same flow control the Media Foundation path needs: libFLAC
            // decodes as fast as it is asked and the socket allows, which can
            // outrun real time and overwrite audio not yet sent.
            while (_buffer.Available >= HighWater && !cancellationToken.IsCancellationRequested)
            {
                Thread.Sleep(5);
            }

            if (!decoder.ReadOne())
            {
                throw new EndOfStreamException(
                    framesDecoded == 0
                        ? $"libFLAC read no audio at all from this station. {decoder.Failure
                            ?? "It gave no reason."}"
                        : $"the station stopped sending. {decoder.Failure ?? ""}".TrimEnd());
            }

            long nowMs = Environment.TickCount64;
            if (nowMs - lastReportMs >= 2000 && sourceRate > 0)
            {
                lastReportMs = nowMs;
                Description =
                    $"{_station} — {label} {sourceRate} Hz, "
                    + $"{framesDecoded / sourceRate} s, peak {Dbfs(peak)}";
                peak = 0f;
            }
        }
    }

    /// <summary>
    /// Opens a station through Media Foundation, or gives up saying so.
    /// </summary>
    /// <remarks>
    /// On its own thread with a deadline, because the constructor is not
    /// guaranteed to come back. It opens the URL, negotiates a media type and
    /// asks the source for its duration — all against a live server, none of
    /// it cancellable, and a station that answers slowly or streams without a
    /// duration can leave it sitting there. A worker thread blocked forever
    /// inside a COM call is the quietest failure this class can have: no
    /// exception to catch, no log line, no reconnect, and a description
    /// frozen at whatever was set before it.
    ///
    /// The abandoned thread is left to finish on its own. It is a background
    /// thread so it cannot hold the Hub open, and there is no supported way
    /// to interrupt the call it is stuck in; leaking one rather than blocking
    /// the station forever is the better of the two.
    /// </remarks>
    private MediaFoundationReader Open(string url, string codec)
    {
        MediaFoundationReader? reader = null;
        Exception? failure = null;
        using var opened = new ManualResetEventSlim(false);

        var opener = new Thread(() =>
        {
            try
            {
                reader = new MediaFoundationReader(url);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                opened.Set();
            }
        })
        {
            IsBackground = true,
            Name = "oal-radio-open",
        };

        opener.Start();

        if (!opened.Wait(OpenTimeout))
        {
            throw new TimeoutException(
                $"Media Foundation did not open this station within "
                + $"{OpenTimeout.TotalSeconds:0} seconds. It was offered as {codec}; a stream "
                + "it cannot make sense of can leave it waiting rather than refusing.");
        }

        if (failure is not null)
        {
            // Wrapped with the codec, because Media Foundation's own messages
            // name an HRESULT and not the station it was given.
            throw new NotSupportedException(
                $"Media Foundation refused this station, offered as {codec}: {failure.Message}",
                failure);
        }

        return reader!;
    }

    /// <summary>A sample magnitude as dBFS, for reading rather than for maths.</summary>
    /// <remarks>
    /// Exactly zero is reported as −∞ rather than as a very large negative
    /// number, because "−∞" is what a person recognises as *nothing at all*
    /// and −144 dB reads like a quiet stream.
    /// </remarks>
    private static string Dbfs(float magnitude) =>
        magnitude <= 0f ? "−∞ dB" : $"{20 * Math.Log10(magnitude):0.0} dB";

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

    private void Decode(CountingStream body, CancellationToken cancellationToken)
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
                            "the station closed the connection before sending a single MP3 "
                            + "frame. It is not MP3, or the playlist pointed somewhere that "
                            + "is not audio at all.");
                    }
                    return;
                }

                frames++;

                // A real frame: the stream is what it claimed to be, and the
                // deadline for finding one has done its job.
                body.SyncSearchLimit = long.MaxValue;

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

        /// <summary>
        /// How many bytes the reader may consume before it has to show
        /// something for them.
        /// </summary>
        /// <remarks>
        /// Here rather than in the decode loop because the decode loop never
        /// gets a turn. <c>Mp3Frame.LoadFromStream</c> hunts for a frame sync
        /// inside a single call, so a check between calls is a check that
        /// never runs — and on a stream that is not MP3 at all, that call
        /// does not come back. What it must do is keep reading, which makes
        /// this the one place the search can be stopped.
        ///
        /// Set to <see cref="long.MaxValue"/> once a real frame has been
        /// decoded; from then on there is nothing left to prove.
        /// </remarks>
        public long SyncSearchLimit { get; set; } = long.MaxValue;

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

            if (_position > SyncSearchLimit)
            {
                throw new NotSupportedException(
                    $"read {_position / 1024} kB of this station without finding a single MP3 "
                    + "frame, so it is not MP3 — whatever its first bytes suggested.");
            }

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

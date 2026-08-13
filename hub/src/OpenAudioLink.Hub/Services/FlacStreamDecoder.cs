using System.Runtime.InteropServices;

namespace OpenAudioLink.Hub.Services;

/// <summary>
/// Xiph's reference FLAC decoder, reading a stream that cannot seek.
/// </summary>
/// <remarks>
/// Added after Media Foundation turned out to be unable to play the FLAC
/// that internet radio actually serves. Windows decodes FLAC in its own
/// container and ships no Ogg demuxer, and Icecast encapsulates FLAC in
/// Ogg — so the stations this was built for are exactly the ones MF cannot
/// open. Worse, it does not refuse: it sits on the open until something
/// times it out.
///
/// libFLAC is the right shape for this in a way nothing tried before it
/// was. Every previous reader assumed something a live stream cannot
/// provide — a position, a length, an end — and had to be worked around.
/// Here, leaving out the seek, tell, length and eof callbacks *is* the
/// supported way to say "this is a pipe", and the decoder stops asking.
/// The same library reads native FLAC and Ogg FLAC, so the container
/// question disappears with it.
///
/// The interop is small but has two edges worth knowing about. Delegates
/// handed to native code must be rooted for as long as the decoder lives,
/// or the collector takes them and the process dies inside a callback with
/// no managed stack to show for it. And no exception may cross back into C:
/// every callback catches everything and turns it into a status code.
/// </remarks>
public sealed class FlacStreamDecoder : IDisposable
{
    /// <summary>
    /// What the library is called, in the order worth trying.
    /// </summary>
    /// <remarks>
    /// It has been both over the years and vcpkg does not promise which.
    /// Resolving by attempt rather than by assumption costs one lookup at
    /// startup and saves a load failure that reads like a missing file.
    /// </remarks>
    private static readonly string[] Names = ["FLAC", "libFLAC", "libFLAC-8", "libFLAC_dynamic"];

    private const string Library = "FLAC";

    static FlacStreamDecoder()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(FlacStreamDecoder).Assembly,
            (name, assembly, path) =>
            {
                if (name != Library)
                {
                    return IntPtr.Zero;
                }

                foreach (var candidate in Names)
                {
                    if (NativeLibrary.TryLoad(candidate, assembly, path, out var handle))
                    {
                        return handle;
                    }
                }

                return IntPtr.Zero;
            });
    }

    // The subset of the C API this needs. Everything else libFLAC offers is
    // about files, seeking and metadata editing, none of which apply to a
    // station that started before we arrived and ends when it feels like it.
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FLAC__stream_decoder_new();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FLAC__stream_decoder_delete(IntPtr decoder);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FLAC__stream_decoder_init_stream(
        IntPtr decoder, ReadCallback read, IntPtr seek, IntPtr tell, IntPtr length,
        IntPtr eof, WriteCallback write, IntPtr metadata, ErrorCallback error, IntPtr clientData);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FLAC__stream_decoder_init_ogg_stream(
        IntPtr decoder, ReadCallback read, IntPtr seek, IntPtr tell, IntPtr length,
        IntPtr eof, WriteCallback write, IntPtr metadata, ErrorCallback error, IntPtr clientData);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FLAC__stream_decoder_process_single(IntPtr decoder);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FLAC__stream_decoder_get_state(IntPtr decoder);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FLAC__stream_decoder_finish(IntPtr decoder);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadCallback(
        IntPtr decoder, IntPtr buffer, ref nuint bytes, IntPtr clientData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WriteCallback(
        IntPtr decoder, IntPtr frame, IntPtr buffer, IntPtr clientData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ErrorCallback(IntPtr decoder, int status, IntPtr clientData);

    private const int ReadContinue = 0;
    private const int ReadEndOfStream = 1;
    private const int ReadAbort = 2;
    private const int WriteContinue = 0;
    private const int WriteAbort = 1;
    private const int InitOk = 0;
    private const int InitUnsupportedContainer = 1;

    /// <summary>
    /// Decoder states past the point of no return. Anything at or beyond
    /// end-of-stream will not produce another frame however often it is
    /// asked, which is the difference between a station that paused and one
    /// that is over.
    /// </summary>
    private const int StateEndOfStream = 4;

    private readonly Stream _source;
    private readonly Action<float[], int, int, int> _onFrames;

    // Rooted for the decoder's whole life. Fields alone are not enough to
    // reason about: the collector cannot see that native code holds these,
    // so the handles say so explicitly.
    private readonly ReadCallback _read;
    private readonly WriteCallback _write;
    private readonly ErrorCallback _error;
    private readonly GCHandle _readHandle;
    private readonly GCHandle _writeHandle;
    private readonly GCHandle _errorHandle;

    private IntPtr _decoder;
    private float[] _interleaved = [];
    private int[] _channel = [];
    private bool _disposed;

    /// <param name="source">
    /// The station, already connected. Read forward only; the decoder is
    /// told it cannot seek, so it never asks.
    /// </param>
    /// <param name="ogg">
    /// Whether the stream is Ogg-encapsulated. Decided by the caller from
    /// the container signature, because by the time libFLAC would tell us,
    /// the bytes are gone.
    /// </param>
    /// <param name="onFrames">
    /// Called with interleaved float samples, the sample count, the source
    /// sample rate and its channel count. Rate and channels come from each
    /// frame rather than from the stream header, because a decoder is
    /// entitled to change its mind and a station is entitled to restart.
    /// </param>
    public FlacStreamDecoder(Stream source, bool ogg, Action<float[], int, int, int> onFrames)
    {
        _source = source;
        _onFrames = onFrames;

        _read = OnRead;
        _write = OnWrite;
        _error = OnError;
        _readHandle = GCHandle.Alloc(_read);
        _writeHandle = GCHandle.Alloc(_write);
        _errorHandle = GCHandle.Alloc(_error);

        _decoder = FLAC__stream_decoder_new();
        if (_decoder == IntPtr.Zero)
        {
            throw new InvalidOperationException("libFLAC would not create a decoder.");
        }

        /*
         * No seek, tell, length or eof callback, and that is the whole
         * point. libFLAC treats their absence as "this source is a pipe"
         * and stops asking questions a live station cannot answer — the
         * questions that defeated every reader tried before it.
         */
        var status = ogg
            ? FLAC__stream_decoder_init_ogg_stream(
                _decoder, _read, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                _write, IntPtr.Zero, _error, IntPtr.Zero)
            : FLAC__stream_decoder_init_stream(
                _decoder, _read, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                _write, IntPtr.Zero, _error, IntPtr.Zero);

        if (status != InitOk)
        {
            Dispose();

            // Named, because this one has a specific cause and a specific
            // cure: a libFLAC built without libogg exports the Ogg entry
            // points and refuses here rather than at load time.
            throw new NotSupportedException(
                status == InitUnsupportedContainer && ogg
                    ? "this libFLAC was built without Ogg support, so it cannot read Ogg FLAC. "
                        + "The build needs libogg."
                    : $"libFLAC refused to start on this stream (init status {status}).");
        }
    }

    /// <summary>The station's failure, if it had one, in its own words.</summary>
    public string? Failure { get; private set; }

    /// <summary>
    /// Decodes one frame, or one metadata block, and returns whether there
    /// may be more.
    /// </summary>
    /// <remarks>
    /// One at a time rather than <c>process_until_end_of_stream</c>, which
    /// would not return until the station did. The caller owns the pacing —
    /// it is the one that knows how full the ring is.
    /// </remarks>
    public bool ReadOne()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (FLAC__stream_decoder_process_single(_decoder) == 0)
        {
            return false;
        }

        return FLAC__stream_decoder_get_state(_decoder) < StateEndOfStream;
    }

    private int OnRead(IntPtr decoder, IntPtr buffer, ref nuint bytes, IntPtr clientData)
    {
        try
        {
            int wanted = (int)Math.Min(bytes, 64 * 1024);
            if (wanted <= 0)
            {
                bytes = 0;
                return ReadAbort;
            }

            var scratch = new byte[wanted];
            int read = _source.Read(scratch, 0, wanted);
            if (read <= 0)
            {
                bytes = 0;
                return ReadEndOfStream;
            }

            Marshal.Copy(scratch, 0, buffer, read);
            bytes = (nuint)read;
            return ReadContinue;
        }
        catch (Exception ex)
        {
            // Nothing may be thrown across this boundary: the stack on the
            // other side is C, and unwinding through it ends the process
            // rather than the request.
            Failure = ex.Message;
            bytes = 0;
            return ReadAbort;
        }
    }

    private int OnWrite(IntPtr decoder, IntPtr frame, IntPtr buffer, IntPtr clientData)
    {
        try
        {
            /*
             * Only the front of FLAC__FrameHeader is read, by offset.
             *
             * The struct continues into a union and an array of subframes
             * whose layout is of no interest here, and describing all of it
             * in C# would be a second thing to keep in step with libFLAC.
             * These four fields have been at the front of the header since
             * the format was published.
             */
            int blockSize = Marshal.ReadInt32(frame, 0);
            int sampleRate = Marshal.ReadInt32(frame, 4);
            int channels = Marshal.ReadInt32(frame, 8);
            int bitsPerSample = Marshal.ReadInt32(frame, 16);

            if (blockSize <= 0 || channels <= 0 || bitsPerSample is <= 0 or > 32)
            {
                Failure = $"libFLAC produced a frame this cannot read: {blockSize} samples, "
                    + $"{channels} channels, {bitsPerSample}-bit.";
                return WriteAbort;
            }

            // Always stereo out, because that is what the wire carries; a
            // mono station is duplicated rather than played from one side.
            int wanted = blockSize * 2;
            if (_interleaved.Length < wanted)
            {
                _interleaved = new float[wanted];
            }
            if (_channel.Length < blockSize)
            {
                _channel = new int[blockSize];
            }

            // Full scale for this depth. FLAC hands back signed integers in
            // the stream's own bit depth, left-justified by nothing at all,
            // so the divisor is the depth's own and not a fixed 32-bit one.
            float scale = 1f / (1 << (bitsPerSample - 1));

            for (int channel = 0; channel < Math.Min(channels, 2); channel++)
            {
                var samples = Marshal.ReadIntPtr(buffer, channel * IntPtr.Size);
                Marshal.Copy(samples, _channel, 0, blockSize);

                for (int i = 0; i < blockSize; i++)
                {
                    _interleaved[i * 2 + channel] = _channel[i] * scale;
                }
            }

            if (channels == 1)
            {
                for (int i = 0; i < blockSize; i++)
                {
                    _interleaved[i * 2 + 1] = _interleaved[i * 2];
                }
            }

            _onFrames(_interleaved, wanted, sampleRate, channels);
            return WriteContinue;
        }
        catch (Exception ex)
        {
            Failure = ex.Message;
            return WriteAbort;
        }
    }

    private void OnError(IntPtr decoder, int status, IntPtr clientData)
    {
        /*
         * Recorded, not thrown, and not fatal.
         *
         * libFLAC reports a lost sync or a bad frame here and then carries
         * on, which is right for a live stream: a station that drops a
         * packet should cost a moment of audio rather than the connection.
         * The caller decides whether a run of these means anything.
         */
        Failure = status switch
        {
            0 => "lost sync",
            1 => "bad frame header",
            2 => "frame CRC mismatch",
            3 => "unparseable stream",
            _ => $"decoder error {status}",
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_decoder != IntPtr.Zero)
        {
            // finish() before delete(), so libFLAC stops calling back into
            // this object before the handles rooting those callbacks go.
            FLAC__stream_decoder_finish(_decoder);
            FLAC__stream_decoder_delete(_decoder);
            _decoder = IntPtr.Zero;
        }

        if (_readHandle.IsAllocated)
        {
            _readHandle.Free();
        }
        if (_writeHandle.IsAllocated)
        {
            _writeHandle.Free();
        }
        if (_errorHandle.IsAllocated)
        {
            _errorHandle.Free();
        }
    }
}

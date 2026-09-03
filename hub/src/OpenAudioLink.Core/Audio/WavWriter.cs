using System.Buffers.Binary;
using System.Text;

namespace OpenAudioLink.Core.Audio;

/// <summary>
/// Writes the profile's L24 payload to a WAV file, unchanged.
/// </summary>
/// <remarks>
/// <para>
/// The stream is already 24-bit little-endian-per-sample... except it is
/// not: RTP L24 is <b>big-endian</b> (RFC 3190) and WAV is little-endian,
/// so every sample's three bytes are reversed on the way through. That
/// swap is the only arithmetic here and it is the thing worth testing —
/// getting it wrong does not fail, it produces a file full of loud static
/// that looks like a broken microphone.
/// </para>
/// <para>
/// Written for measurement rather than for listening, which decides two
/// things. The header is patched on close, so a recording interrupted by a
/// crash still leaves a file the sizes of which are wrong but whose samples
/// are all there. And nothing is resampled, dithered or normalised: what
/// went over the wire is what lands in the file, because an analysis is
/// only as trustworthy as the least-processed copy available to it.
/// </para>
/// </remarks>
public sealed class WavWriter : IDisposable
{
    private const int HeaderBytes = 44;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly int _channels;
    private readonly int _sampleRate;
    private long _dataBytes;
    private bool _closed;

    public WavWriter(Stream stream, int sampleRate, int channels, bool ownsStream = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }
        if (channels is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        _stream = stream;
        _ownsStream = ownsStream;
        _sampleRate = sampleRate;
        _channels = channels;

        // Reserved and patched on close: the sizes are not known until then,
        // and seeking back is cheaper than buffering a whole recording.
        _stream.Write(new byte[HeaderBytes]);
    }

    /// <summary>Samples written so far, per channel.</summary>
    public long Frames => _dataBytes / (3 * _channels);

    /// <summary>How much audio has been written, as a duration.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Frames / _sampleRate);

    /// <summary>
    /// Appends one RTP payload: big-endian 24-bit samples, interleaved.
    /// </summary>
    public void WriteL24(ReadOnlySpan<byte> payload)
    {
        if (payload.Length % 3 != 0)
        {
            throw new ArgumentException(
                "L24 payload must be a whole number of 3-byte samples", nameof(payload));
        }

        Span<byte> flipped = stackalloc byte[3];
        for (int i = 0; i < payload.Length; i += 3)
        {
            /*
             * RFC 3190 puts the most significant byte first; WAV wants it
             * last. Three bytes reversed, per sample, and that is the whole
             * conversion -- L24 and 24-bit PCM WAV are otherwise the same
             * thing.
             */
            flipped[0] = payload[i + 2];
            flipped[1] = payload[i + 1];
            flipped[2] = payload[i];
            _stream.Write(flipped);
        }
        _dataBytes += payload.Length;
    }

    /// <summary>
    /// Appends silence, for packets that never arrived.
    /// </summary>
    /// <remarks>
    /// The reason a recorder for measurement needs this at all: concatenating
    /// what did arrive would leave a file whose time axis is a lie, and every
    /// number derived from it — an arrival time, a delay between two
    /// speakers, the position of a peak — would be wrong by however much was
    /// lost, silently.
    /// </remarks>
    public void WriteSilence(int frames)
    {
        if (frames <= 0)
        {
            return;
        }
        int bytes = frames * 3 * _channels;
        Span<byte> zeros = stackalloc byte[3 * 8];
        while (bytes > 0)
        {
            int take = Math.Min(bytes, zeros.Length);
            _stream.Write(zeros[..take]);
            bytes -= take;
        }
        _dataBytes += (long)frames * 3 * _channels;
    }

    /// <summary>Patches the header with the final sizes.</summary>
    public void Close()
    {
        if (_closed)
        {
            return;
        }
        _closed = true;

        Span<byte> header = stackalloc byte[HeaderBytes];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(36 + _dataBytes));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);   // PCM chunk size
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);    // PCM, uncompressed
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)_channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)_sampleRate);
        int blockAlign = 3 * _channels;
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)(_sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 24);   // bits per sample
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)_dataBytes);

        if (_stream.CanSeek)
        {
            long at = _stream.Position;
            _stream.Position = 0;
            _stream.Write(header);
            _stream.Position = at;
        }
        _stream.Flush();
    }

    public void Dispose()
    {
        Close();
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }
}

using System.Buffers.Binary;

namespace OpenAudioLink.Core.Audio;

/// <summary>
/// Builds RTP audio packets per protocol/AUDIO-RTP.md: a 12-byte RTP
/// header (RFC 3550) followed by big-endian linear PCM.
///
/// The timestamp is a media clock — it advances by exactly the number of
/// frames written, never by wall-clock time — so a late or bursty sender
/// still produces a correctly timed stream.
/// </summary>
public sealed class RtpAudioPacketizer
{
    private readonly AudioStreamFormat _format;
    private readonly byte _payloadType;
    private readonly uint _ssrc;

    private ushort _sequence;
    private uint _timestamp;
    private bool _startOfStream = true;

    public RtpAudioPacketizer(
        AudioStreamFormat format,
        byte payloadType = 96,
        uint? ssrc = null,
        ushort? initialSequence = null,
        uint? initialTimestamp = null)
    {
        ArgumentNullException.ThrowIfNull(format);
        format.Validate();

        if (payloadType > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadType), "RTP payload type must be 0-127.");
        }

        _format = format;
        _payloadType = payloadType;
        _ssrc = ssrc ?? (uint)Random.Shared.Next(int.MinValue, int.MaxValue);
        _sequence = initialSequence ?? (ushort)Random.Shared.Next(ushort.MaxValue + 1);
        _timestamp = initialTimestamp ?? (uint)Random.Shared.Next(int.MinValue, int.MaxValue);
    }

    public uint Ssrc => _ssrc;

    public ushort NextSequence => _sequence;

    public uint NextTimestamp => _timestamp;

    /// <summary>
    /// Marks the next packet as the start of a stream, so receivers reset
    /// their jitter buffers instead of treating a gap as loss.
    /// </summary>
    public void MarkDiscontinuity() => _startOfStream = true;

    /// <summary>
    /// Writes one packet from interleaved samples in the range [-1, 1].
    /// Samples outside the range are clipped rather than wrapped.
    /// </summary>
    /// <returns>The number of bytes written to <paramref name="destination"/>.</returns>
    public int WritePacket(ReadOnlySpan<float> frames, Span<byte> destination)
    {
        if (frames.Length != _format.SamplesPerPacket)
        {
            throw new ArgumentException(
                $"Expected {_format.SamplesPerPacket} samples for one packet but got {frames.Length}.",
                nameof(frames));
        }

        if (destination.Length < _format.PacketBytes)
        {
            throw new ArgumentException(
                $"Destination needs {_format.PacketBytes} bytes but has {destination.Length}.",
                nameof(destination));
        }

        WriteHeader(destination);
        WritePayload(frames, destination[AudioStreamFormat.RtpHeaderBytes..]);

        unchecked
        {
            _sequence++;
            _timestamp += (uint)_format.FramesPerPacket;
        }
        _startOfStream = false;

        return _format.PacketBytes;
    }

    private void WriteHeader(Span<byte> destination)
    {
        destination[0] = 0x80; // version 2, no padding, no extension, no CSRCs
        destination[1] = (byte)(_startOfStream ? _payloadType | 0x80 : _payloadType);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], _sequence);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], _timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], _ssrc);
    }

    private void WritePayload(ReadOnlySpan<float> frames, Span<byte> destination)
    {
        if (_format.Encoding == PcmEncoding.L24)
        {
            for (int i = 0, offset = 0; i < frames.Length; i++, offset += 3)
            {
                int sample = ScaleAndClip(frames[i], 8388608f, -8388608, 8388607);
                destination[offset] = (byte)(sample >> 16);
                destination[offset + 1] = (byte)(sample >> 8);
                destination[offset + 2] = (byte)sample;
            }
        }
        else
        {
            for (int i = 0, offset = 0; i < frames.Length; i++, offset += 2)
            {
                int sample = ScaleAndClip(frames[i], 32768f, short.MinValue, short.MaxValue);
                destination[offset] = (byte)(sample >> 8);
                destination[offset + 1] = (byte)sample;
            }
        }
    }

    /// <summary>
    /// Compares in float space before converting, so out-of-range or NaN
    /// input clips cleanly instead of wrapping through an invalid cast.
    /// </summary>
    private static int ScaleAndClip(float sample, float scale, int min, int max)
    {
        if (float.IsNaN(sample))
        {
            return 0;
        }

        float scaled = MathF.Round(sample * scale);
        return scaled <= min ? min : scaled >= max ? max : (int)scaled;
    }
}

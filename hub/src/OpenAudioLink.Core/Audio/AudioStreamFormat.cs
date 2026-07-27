namespace OpenAudioLink.Core.Audio;

/// <summary>Linear PCM encodings carried by RTP (protocol/AUDIO-RTP.md).</summary>
public enum PcmEncoding
{
    /// <summary>24-bit linear PCM, RFC 3190. The reference format.</summary>
    L24,

    /// <summary>16-bit linear PCM, RFC 3551. Compatibility fallback.</summary>
    L16,
}

/// <summary>
/// Wire format of an audio stream. Defaults are the OpenAudioLink
/// reference format: stereo, 48 kHz, 24-bit, 5 ms packets.
/// </summary>
public sealed record AudioStreamFormat
{
    public const int RtpHeaderBytes = 12;

    /// <summary>Largest payload that avoids IP fragmentation on a 1500-byte MTU.</summary>
    public const int MaxPayloadBytes = 1440;

    public int SampleRate { get; init; } = 48000;

    public int Channels { get; init; } = 2;

    public PcmEncoding Encoding { get; init; } = PcmEncoding.L24;

    public int PacketMilliseconds { get; init; } = 5;

    public int BytesPerSample => Encoding == PcmEncoding.L24 ? 3 : 2;

    public int FramesPerPacket => SampleRate * PacketMilliseconds / 1000;

    public int SamplesPerPacket => FramesPerPacket * Channels;

    public int PayloadBytes => SamplesPerPacket * BytesPerSample;

    public int PacketBytes => RtpHeaderBytes + PayloadBytes;

    /// <summary>Encoding name as it appears in an SDP <c>a=rtpmap</c> line.</summary>
    public string RtpmapName => Encoding == PcmEncoding.L24 ? "L24" : "L16";

    /// <summary>
    /// Throws when the format cannot be packetised: a packet must contain a
    /// whole number of frames and must fit inside the MTU.
    /// </summary>
    public void Validate()
    {
        if (SampleRate <= 0 || Channels <= 0 || PacketMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SampleRate),
                "Sample rate, channels and packet time must all be positive.");
        }

        if (SampleRate * PacketMilliseconds % 1000 != 0)
        {
            throw new ArgumentException(
                $"A {PacketMilliseconds} ms packet at {SampleRate} Hz is not a whole number of frames.",
                nameof(PacketMilliseconds));
        }

        if (PayloadBytes > MaxPayloadBytes)
        {
            throw new ArgumentException(
                $"Payload of {PayloadBytes} bytes exceeds the {MaxPayloadBytes}-byte limit; use a shorter packet time.",
                nameof(PacketMilliseconds));
        }
    }
}

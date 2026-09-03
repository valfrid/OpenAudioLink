using System.Buffers.Binary;
using System.Text;
using OpenAudioLink.Core.Audio;
using Xunit;

namespace OpenAudioLink.Core.Tests;

public class WavWriterTests
{
    private static byte[] Write(Action<WavWriter> body, int rate = 48000, int channels = 2)
    {
        var stream = new MemoryStream();
        using (var w = new WavWriter(stream, rate, channels, ownsStream: false))
        {
            body(w);
        }
        return stream.ToArray();
    }

    [Fact]
    public void The_header_says_what_the_profile_is()
    {
        var bytes = Write(w => w.WriteL24(new byte[240 * 2 * 3]));

        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(bytes, 12, 4));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20)));    // PCM
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)));    // stereo
        Assert.Equal(48000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));
        Assert.Equal(6, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(32)));    // block align
        Assert.Equal(24, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34)));   // bits
        Assert.Equal("data", Encoding.ASCII.GetString(bytes, 36, 4));

        var data = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40));
        Assert.Equal((uint)(240 * 2 * 3), data);
        Assert.Equal(36u + data, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
    }

    /// <summary>
    /// The one piece of arithmetic in the class, and the one that does not
    /// fail loudly when it is wrong: RTP L24 is big-endian (RFC 3190), WAV
    /// is little-endian, and a missing swap is a file full of static that
    /// looks exactly like a broken microphone.
    /// </summary>
    [Fact]
    public void L24_is_byte_swapped_into_wav_order()
    {
        // One sample, 0x123456 most-significant-byte-first on the wire.
        var bytes = Write(w => w.WriteL24([0x12, 0x34, 0x56]), channels: 1);

        Assert.Equal(0x56, bytes[44]);
        Assert.Equal(0x34, bytes[45]);
        Assert.Equal(0x12, bytes[46]);
    }

    [Fact]
    public void Silence_takes_the_place_of_what_never_arrived()
    {
        var bytes = Write(w =>
        {
            w.WriteL24([0x7F, 0xFF, 0xFF]);
            w.WriteSilence(2);            // 2 frames x 1 channel x 3 bytes
        }, channels: 1);

        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
        Assert.All(bytes[47..53].ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void The_duration_counts_frames_not_bytes()
    {
        var stream = new MemoryStream();
        using var w = new WavWriter(stream, 48000, 2, ownsStream: false);

        w.WriteL24(new byte[48000 * 2 * 3]);          // one second, stereo
        Assert.Equal(48000, w.Frames);
        Assert.Equal(1.0, w.Duration.TotalSeconds, 6);

        w.WriteSilence(24000);
        Assert.Equal(1.5, w.Duration.TotalSeconds, 6);
    }

    [Fact]
    public void A_payload_that_is_not_whole_samples_is_refused()
    {
        var stream = new MemoryStream();
        using var w = new WavWriter(stream, 48000, 2, ownsStream: false);
        Assert.Throws<ArgumentException>(() => w.WriteL24(new byte[4]));
    }

    /// <summary>
    /// Closing twice happens whenever a caller both disposes and stops a
    /// recording, which is the ordinary path through the recorder.
    /// </summary>
    [Fact]
    public void Closing_twice_does_not_corrupt_the_header()
    {
        var stream = new MemoryStream();
        var w = new WavWriter(stream, 48000, 2, ownsStream: false);
        w.WriteL24(new byte[6]);
        w.Close();
        var once = stream.ToArray();
        w.Close();

        Assert.Equal(once, stream.ToArray());
    }
}

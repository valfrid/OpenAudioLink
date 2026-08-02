using OpenAudioLink.Core.Audio;
using Xunit;

namespace OpenAudioLink.Core.Tests;

public class PcmDecoderTests
{
    [Fact]
    public void Decodes_16_bit_full_scale_and_silence()
    {
        // -32768, 0, 32767 little-endian
        byte[] raw = [0x00, 0x80, 0x00, 0x00, 0xFF, 0x7F];
        var samples = new float[3];

        Assert.Equal(3, PcmDecoder.Decode(raw, samples, PcmSampleFormat.S16));
        Assert.Equal(-1f, samples[0]);
        Assert.Equal(0f, samples[1]);
        Assert.Equal(1.0, samples[2], 4);
    }

    /// <summary>
    /// The sign lives in the third byte. Reading it unsigned turns every
    /// negative sample positive, which is the loudest possible bug and the
    /// reason this case is spelled out.
    /// </summary>
    [Fact]
    public void Decodes_packed_24_bit_including_negatives()
    {
        // -8388608, -1, 0, 8388607
        byte[] raw =
        [
            0x00, 0x00, 0x80,
            0xFF, 0xFF, 0xFF,
            0x00, 0x00, 0x00,
            0xFF, 0xFF, 0x7F,
        ];
        var samples = new float[4];

        Assert.Equal(4, PcmDecoder.Decode(raw, samples, PcmSampleFormat.S24_3));
        Assert.Equal(-1f, samples[0]);
        Assert.Equal(-1f / 8388608f, samples[1]);
        Assert.Equal(0f, samples[2]);
        Assert.Equal(1.0, samples[3], 5);
    }

    [Fact]
    public void Decodes_32_bit_integers()
    {
        byte[] raw = [0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00, 0x40];
        var samples = new float[2];

        Assert.Equal(2, PcmDecoder.Decode(raw, samples, PcmSampleFormat.S32));
        Assert.Equal(-1f, samples[0]);
        Assert.Equal(0.5f, samples[1]);
    }

    [Fact]
    public void Decodes_floats_unchanged()
    {
        var raw = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(raw, -0.25f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(4), 0.75f);
        var samples = new float[2];

        Assert.Equal(2, PcmDecoder.Decode(raw, samples, PcmSampleFormat.F32));
        Assert.Equal(-0.25f, samples[0]);
        Assert.Equal(0.75f, samples[1]);
    }

    /// <summary>
    /// A pipe read ends wherever the pipe ended, so a trailing partial
    /// sample is normal. Decoding it would emit a garbage sample and, worse,
    /// shift every later sample by a byte — a channel swap that never
    /// recovers.
    /// </summary>
    [Fact]
    public void Leaves_a_trailing_partial_sample_alone()
    {
        byte[] raw = [0x00, 0x00, 0x80, 0x11, 0x22];
        var samples = new float[4];

        Assert.Equal(1, PcmDecoder.Decode(raw, samples, PcmSampleFormat.S24_3));
        Assert.Equal(-1f, samples[0]);
    }

    [Fact]
    public void Stops_when_the_destination_is_full()
    {
        byte[] raw = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        var samples = new float[2];

        Assert.Equal(2, PcmDecoder.Decode(raw, samples, PcmSampleFormat.S16));
    }

    [Theory]
    [InlineData("s16", PcmSampleFormat.S16)]
    [InlineData("S24_3", PcmSampleFormat.S24_3)]
    [InlineData("s24-3", PcmSampleFormat.S24_3)]
    [InlineData(" F32 ", PcmSampleFormat.F32)]
    public void Parses_the_names_librespot_uses(string name, PcmSampleFormat expected)
    {
        Assert.True(PcmDecoder.TryParse(name, out var format));
        Assert.Equal(expected, format);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("F64")]
    [InlineData("S24")]
    public void Rejects_formats_it_cannot_decode(string? name)
    {
        // F64 and S24 are things librespot will happily emit; refusing them
        // here is better than decoding them as something else.
        Assert.False(PcmDecoder.TryParse(name, out _));
    }

    [Fact]
    public void Round_trips_every_format_name()
    {
        foreach (var format in Enum.GetValues<PcmSampleFormat>())
        {
            Assert.True(PcmDecoder.TryParse(PcmDecoder.ToLibrespotName(format), out var parsed));
            Assert.Equal(format, parsed);
        }
    }
}

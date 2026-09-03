using System.Buffers.Binary;
using System.Text;
using OpenAudioLink.Core.Audio;
using Xunit;

namespace OpenAudioLink.Core.Tests;

public class WavReaderTests
{
    /// <summary>
    /// The pair that matters: whatever the recorder wrote, the analyser
    /// reads back. A byte-order mistake in either one is a file of static
    /// that looks exactly like a broken microphone, and having both ends
    /// here is what makes the mistake impossible to have in only one.
    /// </summary>
    [Fact]
    public void What_the_recorder_wrote_is_what_comes_back()
    {
        // L24 big-endian on the wire, as RTP carries it.
        var payload = new byte[]
        {
            0x40, 0x00, 0x00,   // left  = 0x400000 =  0.5
            0xC0, 0x00, 0x00,   // right = -0x400000 = -0.5
            0x00, 0x00, 0x00,
            0x7F, 0xFF, 0xFF,   // right = full scale
        };

        var stream = new MemoryStream();
        using (var writer = new WavWriter(stream, 48000, 2, ownsStream: false))
        {
            writer.WriteL24(payload);
        }
        stream.Position = 0;

        var left = WavReader.Read(stream, channel: 0);
        stream.Position = 0;
        var right = WavReader.Read(stream, channel: 1);

        Assert.Equal(48000, left.SampleRate);
        Assert.Equal(2, left.Channels);
        Assert.Equal(2, left.Samples.Length);

        Assert.Equal(0.5, left.Samples[0], 6);
        Assert.Equal(0.0, left.Samples[1], 6);
        Assert.Equal(-0.5, right.Samples[0], 6);
        Assert.Equal(1.0, right.Samples[1], 6);
    }

    [Fact]
    public void Silence_written_for_a_lost_packet_reads_as_silence()
    {
        var stream = new MemoryStream();
        using (var writer = new WavWriter(stream, 48000, 1, ownsStream: false))
        {
            writer.WriteL24([0x7F, 0xFF, 0xFF]);
            writer.WriteSilence(3);
        }
        stream.Position = 0;

        var audio = WavReader.Read(stream);

        Assert.Equal(4, audio.Samples.Length);
        Assert.Equal(1.0, audio.Samples[0], 6);
        Assert.Equal([0.0, 0.0, 0.0], audio.Samples[1..]);
    }

    /// <summary>
    /// The first question about any measurement recording, answered
    /// alongside the audio so nobody has to remember to ask it: a clipped
    /// sweep measures the clipping, and the curve looks like a real one.
    /// </summary>
    [Fact]
    public void The_peak_and_the_clipping_come_back_with_the_audio()
    {
        var stream = new MemoryStream();
        using (var writer = new WavWriter(stream, 48000, 1, ownsStream: false))
        {
            writer.WriteL24([0x10, 0x00, 0x00]);   // 1/8 scale
            writer.WriteL24([0x7F, 0xFF, 0xFF]);   // full scale
            writer.WriteL24([0x80, 0x00, 0x00]);   // full scale, negative
        }
        stream.Position = 0;

        var audio = WavReader.Read(stream);

        Assert.Equal(1.0, audio.PeakLevel, 6);
        Assert.Equal(0.0, audio.PeakDbFs, 3);
        Assert.Equal(2, audio.ClippedSamples);
    }

    [Fact]
    public void A_quiet_file_reports_how_quiet()
    {
        var stream = new MemoryStream();
        using (var writer = new WavWriter(stream, 48000, 1, ownsStream: false))
        {
            writer.WriteL24([0x08, 0x00, 0x00]);   // 1/16 scale = -24.08 dBFS
        }
        stream.Position = 0;

        var audio = WavReader.Read(stream);

        Assert.Equal(-24.08, audio.PeakDbFs, 2);
        Assert.Equal(0, audio.ClippedSamples);
    }

    [Fact]
    public void The_duration_counts_frames()
    {
        var stream = new MemoryStream();
        using (var writer = new WavWriter(stream, 48000, 2, ownsStream: false))
        {
            writer.WriteSilence(24000);
        }
        stream.Position = 0;

        Assert.Equal(0.5, WavReader.Read(stream).Duration.TotalSeconds, 6);
    }

    /// <summary>
    /// Files from other tools carry LIST, fact and JUNK chunks ahead of the
    /// audio. Reading at a fixed byte 44 gets a chunk header where the
    /// first sample should be, which sounds like a click and then noise.
    /// </summary>
    [Fact]
    public void Chunks_it_does_not_know_are_stepped_over()
    {
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        void Tag(string s) => writer.Write(Encoding.ASCII.GetBytes(s));

        Tag("RIFF"); writer.Write(0u); Tag("WAVE");

        Tag("JUNK"); writer.Write(6u); writer.Write(new byte[6]);

        Tag("fmt "); writer.Write(16u);
        writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write(48000u); writer.Write(48000u * 3);
        writer.Write((ushort)3); writer.Write((ushort)24);

        // Odd length, so the word-alignment padding has to be honoured too.
        Tag("LIST"); writer.Write(5u); writer.Write(new byte[5]); writer.Write((byte)0);

        Tag("data"); writer.Write(6u);
        writer.Write(new byte[] { 0x00, 0x00, 0x40, 0x00, 0x00, 0x20 });
        writer.Flush();
        stream.Position = 0;

        var audio = WavReader.Read(stream);

        Assert.Equal(48000, audio.SampleRate);
        Assert.Equal(2, audio.Samples.Length);
        Assert.Equal(0.5, audio.Samples[0], 6);
        Assert.Equal(0.25, audio.Samples[1], 6);
    }

    [Fact]
    public void Sixteen_bit_is_read_too()
    {
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        void Tag(string s) => writer.Write(Encoding.ASCII.GetBytes(s));

        Tag("RIFF"); writer.Write(0u); Tag("WAVE");
        Tag("fmt "); writer.Write(16u);
        writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write(48000u); writer.Write(96000u);
        writer.Write((ushort)2); writer.Write((ushort)16);
        Tag("data"); writer.Write(4u);
        var data = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(data, 16384);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(2), -16384);
        writer.Write(data);
        writer.Flush();
        stream.Position = 0;

        var audio = WavReader.Read(stream);

        Assert.Equal(0.5, audio.Samples[0], 6);
        Assert.Equal(-0.5, audio.Samples[1], 6);
    }

    [Fact]
    public void A_channel_the_file_does_not_have_is_refused()
    {
        var stream = new MemoryStream();
        using (var writer = new WavWriter(stream, 48000, 2, ownsStream: false))
        {
            writer.WriteSilence(10);
        }
        stream.Position = 0;

        Assert.Throws<ArgumentOutOfRangeException>(() => WavReader.Read(stream, channel: 2));
    }

    [Fact]
    public void Something_that_is_not_a_wav_file_is_refused()
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes("this is not a wav file at all"));
        Assert.Throws<InvalidDataException>(() => WavReader.Read(stream));
    }

    [Fact]
    public void A_compressed_wav_file_is_refused_rather_than_read_as_noise()
    {
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        void Tag(string s) => writer.Write(Encoding.ASCII.GetBytes(s));

        Tag("RIFF"); writer.Write(0u); Tag("WAVE");
        Tag("fmt "); writer.Write(16u);
        writer.Write((ushort)0x0011);          // IMA ADPCM
        writer.Write((ushort)1); writer.Write(48000u); writer.Write(24000u);
        writer.Write((ushort)1); writer.Write((ushort)4);
        Tag("data"); writer.Write(4u); writer.Write(new byte[4]);
        writer.Flush();
        stream.Position = 0;

        var ex = Assert.Throws<InvalidDataException>(() => WavReader.Read(stream));
        Assert.Contains("linear PCM", ex.Message);
    }
}

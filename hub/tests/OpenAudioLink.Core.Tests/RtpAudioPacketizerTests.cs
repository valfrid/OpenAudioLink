using System.Buffers.Binary;
using OpenAudioLink.Core.Audio;
using Xunit;

namespace OpenAudioLink.Core.Tests;

public class RtpAudioPacketizerTests
{
    private static readonly AudioStreamFormat Reference = new();

    private static RtpAudioPacketizer Deterministic(AudioStreamFormat? format = null, byte payloadType = 96) =>
        new(format ?? Reference, payloadType, ssrc: 0x11223344, initialSequence: 1000, initialTimestamp: 5000);

    [Fact]
    public void Reference_format_is_stereo_48k_24bit_5ms()
    {
        Assert.Equal(48000, Reference.SampleRate);
        Assert.Equal(2, Reference.Channels);
        Assert.Equal("L24", Reference.RtpmapName);
        Assert.Equal(240, Reference.FramesPerPacket);
        Assert.Equal(1440, Reference.PayloadBytes);
        Assert.Equal(1452, Reference.PacketBytes);
        Reference.Validate();
    }

    [Fact]
    public void Header_carries_version_payload_type_and_ssrc()
    {
        var packetizer = Deterministic();
        var packet = new byte[Reference.PacketBytes];

        packetizer.WritePacket(new float[Reference.SamplesPerPacket], packet);

        Assert.Equal(0x80, packet[0]);                                  // version 2, no padding/extension/CSRC
        Assert.Equal(96, packet[1] & 0x7F);                             // payload type
        Assert.Equal(0x80, packet[1] & 0x80);                           // marker set on first packet
        Assert.Equal(1000, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));
        Assert.Equal(5000u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)));
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8)));
    }

    [Fact]
    public void Marker_is_set_only_on_the_first_packet()
    {
        var packetizer = Deterministic();
        var packet = new byte[Reference.PacketBytes];
        var silence = new float[Reference.SamplesPerPacket];

        packetizer.WritePacket(silence, packet);
        Assert.Equal(0x80, packet[1] & 0x80);

        packetizer.WritePacket(silence, packet);
        Assert.Equal(0, packet[1] & 0x80);

        packetizer.MarkDiscontinuity();
        packetizer.WritePacket(silence, packet);
        Assert.Equal(0x80, packet[1] & 0x80);
    }

    [Fact]
    public void Sequence_and_timestamp_advance_per_packet()
    {
        var packetizer = Deterministic();
        var packet = new byte[Reference.PacketBytes];
        var silence = new float[Reference.SamplesPerPacket];

        packetizer.WritePacket(silence, packet);
        packetizer.WritePacket(silence, packet);

        Assert.Equal(1001, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));
        Assert.Equal(5000u + 240u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)));
        Assert.Equal(1002, packetizer.NextSequence);
        Assert.Equal(5000u + 480u, packetizer.NextTimestamp);
    }

    [Fact]
    public void Sequence_and_timestamp_wrap_without_overflow()
    {
        var packetizer = new RtpAudioPacketizer(
            Reference, ssrc: 1, initialSequence: ushort.MaxValue, initialTimestamp: uint.MaxValue);
        var packet = new byte[Reference.PacketBytes];

        packetizer.WritePacket(new float[Reference.SamplesPerPacket], packet);

        Assert.Equal(0, packetizer.NextSequence);
        Assert.Equal(239u, packetizer.NextTimestamp); // uint.MaxValue + 240, wrapped
    }

    /// <summary>
    /// The single most likely defect in the audio path: RTP L24 is
    /// big-endian while Windows, I2S and the ESP32 are little-endian.
    /// </summary>
    [Theory]
    [InlineData(1.0f, 0x7F, 0xFF, 0xFF)]      // full scale positive, clipped to 2^23-1
    [InlineData(-1.0f, 0x80, 0x00, 0x00)]     // full scale negative, -2^23
    [InlineData(0.0f, 0x00, 0x00, 0x00)]      // silence
    [InlineData(0.5f, 0x40, 0x00, 0x00)]      // half scale, MSB first
    [InlineData(-0.5f, 0xC0, 0x00, 0x00)]     // two's complement, MSB first
    public void L24_samples_are_big_endian(float sample, byte first, byte second, byte third)
    {
        var packetizer = Deterministic();
        var packet = new byte[Reference.PacketBytes];
        var samples = new float[Reference.SamplesPerPacket];
        Array.Fill(samples, sample);

        packetizer.WritePacket(samples, packet);

        Assert.Equal(first, packet[AudioStreamFormat.RtpHeaderBytes]);
        Assert.Equal(second, packet[AudioStreamFormat.RtpHeaderBytes + 1]);
        Assert.Equal(third, packet[AudioStreamFormat.RtpHeaderBytes + 2]);
    }

    [Theory]
    [InlineData(1.0f, 0x7F, 0xFF)]
    [InlineData(-1.0f, 0x80, 0x00)]
    [InlineData(0.5f, 0x40, 0x00)]
    public void L16_samples_are_big_endian(float sample, byte first, byte second)
    {
        var format = new AudioStreamFormat { Encoding = PcmEncoding.L16 };
        var packetizer = Deterministic(format);
        var packet = new byte[format.PacketBytes];
        var samples = new float[format.SamplesPerPacket];
        Array.Fill(samples, sample);

        packetizer.WritePacket(samples, packet);

        Assert.Equal(first, packet[AudioStreamFormat.RtpHeaderBytes]);
        Assert.Equal(second, packet[AudioStreamFormat.RtpHeaderBytes + 1]);
        Assert.Equal(960, format.PayloadBytes);
    }

    [Theory]
    [InlineData(5.0f)]
    [InlineData(-5.0f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Out_of_range_input_clips_rather_than_wrapping(float sample)
    {
        var packetizer = Deterministic();
        var packet = new byte[Reference.PacketBytes];
        var samples = new float[Reference.SamplesPerPacket];
        Array.Fill(samples, sample);

        packetizer.WritePacket(samples, packet);

        // Reconstruct the 24-bit two's complement value and check it is in range.
        int value = (packet[12] << 16) | (packet[13] << 8) | packet[14];
        if ((value & 0x800000) != 0)
        {
            value -= 0x1000000;
        }
        Assert.InRange(value, -8388608, 8388607);
    }

    [Fact]
    public void Channels_stay_interleaved_left_then_right()
    {
        var packetizer = Deterministic();
        var packet = new byte[Reference.PacketBytes];
        var samples = new float[Reference.SamplesPerPacket];
        for (int frame = 0; frame < Reference.FramesPerPacket; frame++)
        {
            samples[frame * 2] = 0.5f;      // left
            samples[frame * 2 + 1] = -0.5f; // right
        }

        packetizer.WritePacket(samples, packet);

        Assert.Equal(0x40, packet[12]); // first sample is left
        Assert.Equal(0xC0, packet[15]); // second sample is right
    }

    [Fact]
    public void Wrong_sample_count_is_rejected()
    {
        var packetizer = Deterministic();
        var packet = new byte[Reference.PacketBytes];

        Assert.Throws<ArgumentException>(() => packetizer.WritePacket(new float[10], packet));
    }

    [Fact]
    public void Undersized_destination_is_rejected()
    {
        var packetizer = Deterministic();

        Assert.Throws<ArgumentException>(() =>
            packetizer.WritePacket(new float[Reference.SamplesPerPacket], new byte[100]));
    }
}

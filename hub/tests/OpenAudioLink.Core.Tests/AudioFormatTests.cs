using OpenAudioLink.Core.Audio;
using Xunit;

namespace OpenAudioLink.Core.Tests;

public class AudioFormatTests
{
    [Fact]
    public void One_millisecond_packets_match_the_AES67_baseline()
    {
        var format = new AudioStreamFormat { PacketMilliseconds = 1 };

        format.Validate();
        Assert.Equal(48, format.FramesPerPacket);
        Assert.Equal(288, format.PayloadBytes);
    }

    [Fact]
    public void Packet_time_that_is_not_a_whole_number_of_frames_is_rejected()
    {
        var format = new AudioStreamFormat { SampleRate = 44100, PacketMilliseconds = 5 };

        Assert.Throws<ArgumentException>(format.Validate);
    }

    [Fact]
    public void Packet_time_that_would_fragment_is_rejected()
    {
        var format = new AudioStreamFormat { PacketMilliseconds = 10 };

        // 10 ms of L24 stereo is 2880 bytes, well over a 1500-byte MTU.
        Assert.Throws<ArgumentException>(format.Validate);
    }

    [Fact]
    public void Sdp_describes_the_stream_for_standard_receivers()
    {
        var sdp = SessionDescription.Build(
            new AudioStreamFormat(), "192.168.1.10", "192.168.1.40", 41100,
            sessionName: "OpenAudioLink Kitchen");

        Assert.Contains("m=audio 41100 RTP/AVP 96\r\n", sdp);
        Assert.Contains("a=rtpmap:96 L24/48000/2\r\n", sdp);
        Assert.Contains("a=ptime:5\r\n", sdp);
        Assert.Contains("c=IN IP4 192.168.1.40\r\n", sdp);
        Assert.Contains("s=OpenAudioLink Kitchen\r\n", sdp);
        Assert.StartsWith("v=0\r\n", sdp);
    }

    [Fact]
    public void Sdp_reflects_the_L16_fallback()
    {
        var sdp = SessionDescription.Build(
            new AudioStreamFormat { Encoding = PcmEncoding.L16 }, "10.0.0.1", "10.0.0.2", 41100);

        Assert.Contains("a=rtpmap:96 L16/48000/2\r\n", sdp);
    }

    [Fact]
    public void Tone_fills_every_channel_and_stays_in_range()
    {
        var format = new AudioStreamFormat();
        var tone = new SineToneSource(format, frequencyHz: 1000.0, amplitude: 0.5f);
        var samples = new float[format.SamplesPerPacket];

        tone.ReadFrames(samples);

        Assert.All(samples, s => Assert.InRange(s, -0.5001f, 0.5001f));
        for (int frame = 0; frame < format.FramesPerPacket; frame++)
        {
            Assert.Equal(samples[frame * 2], samples[frame * 2 + 1]);
        }
    }

    [Fact]
    public void Tone_phase_is_continuous_across_reads()
    {
        var format = new AudioStreamFormat();
        var tone = new SineToneSource(format, frequencyHz: 1000.0);

        // 1 kHz at 48 kHz repeats every 48 frames, so a 240-frame packet is
        // exactly five cycles: consecutive packets must be identical.
        var first = new float[format.SamplesPerPacket];
        var second = new float[format.SamplesPerPacket];
        tone.ReadFrames(first);
        tone.ReadFrames(second);

        for (int i = 0; i < first.Length; i++)
        {
            Assert.Equal((double)first[i], (double)second[i], 4);
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-100.0)]
    [InlineData(24000.0)]
    public void Tone_rejects_frequencies_outside_the_audible_band(double frequencyHz)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SineToneSource(new AudioStreamFormat(), frequencyHz));
    }
}

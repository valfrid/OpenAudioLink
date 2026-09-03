using OpenAudioLink.Core.Audio;
using OpenAudioLink.Core.Devices;
using Xunit;

namespace OpenAudioLink.Core.Tests;

public class SweepSourceTests
{
    private static readonly AudioStreamFormat Format = new();

    private static SweepSignal Short() =>
        new() { SweepSeconds = 0.2, SilenceSeconds = 0.05, FadeInSeconds = 0.01, FadeOutSeconds = 0.005 };

    /// <summary>
    /// The reason the channel is a required thought rather than a default.
    /// Two speakers playing one sweep arrive at the microphone at different
    /// times, and their sum cancels at frequencies that belong to the pair
    /// and not to either speaker — a correction fitted to that would make
    /// both of them worse.
    /// </summary>
    [Fact]
    public void The_other_speaker_gets_silence()
    {
        using var source = new SweepSource(Format, Short(), AudioChannel.Left);
        var frames = new float[240 * 2];
        source.ReadFrames(frames);

        bool anythingOnTheLeft = false;
        for (int i = 0; i < frames.Length; i += 2)
        {
            Assert.Equal(0f, frames[i + 1]);
            anythingOnTheLeft |= frames[i] != 0f;
        }
        Assert.True(anythingOnTheLeft);
    }

    [Fact]
    public void The_right_channel_is_the_mirror_of_it()
    {
        using var source = new SweepSource(Format, Short(), AudioChannel.Right);
        var frames = new float[240 * 2];
        source.ReadFrames(frames);

        bool anythingOnTheRight = false;
        for (int i = 0; i < frames.Length; i += 2)
        {
            Assert.Equal(0f, frames[i]);
            anythingOnTheRight |= frames[i + 1] != 0f;
        }
        Assert.True(anythingOnTheRight);
    }

    [Fact]
    public void Stereo_puts_the_same_sweep_on_both()
    {
        using var source = new SweepSource(Format, Short(), AudioChannel.Stereo);
        var frames = new float[240 * 2];
        source.ReadFrames(frames);

        for (int i = 0; i < frames.Length; i += 2)
        {
            Assert.Equal(frames[i], frames[i + 1]);
        }
    }

    /// <summary>
    /// Packets are pulled one at a time, and the signal is a function of
    /// the absolute frame index — so what a packet contains has to depend
    /// on where it sits in the stream, not on which call produced it.
    /// </summary>
    [Fact]
    public void The_position_carries_across_calls()
    {
        var signal = Short();
        using var source = new SweepSource(Format, signal, AudioChannel.Stereo);

        var first = new float[240 * 2];
        var second = new float[240 * 2];
        source.ReadFrames(first);
        source.ReadFrames(second);

        Assert.Equal(480, source.FramesEmitted);
        for (int i = 0; i < 240; i++)
        {
            Assert.Equal((float)signal.SampleAt(i), first[i * 2]);
            Assert.Equal((float)signal.SampleAt(240 + i), second[i * 2]);
        }
    }

    [Fact]
    public void Complete_looks_at_the_room_are_counted()
    {
        var signal = Short();
        using var source = new SweepSource(Format, signal, AudioChannel.Stereo);

        var packet = new float[240 * 2];
        Assert.Equal(0, source.CyclesEmitted);

        // Two whole cycles plus a fraction.
        int packets = 2 * signal.CycleFrames / 240 + 3;
        for (int i = 0; i < packets; i++)
        {
            source.ReadFrames(packet);
        }
        Assert.Equal(2, source.CyclesEmitted);
    }

    /// <summary>
    /// A resampled sweep is a sweep between two other frequencies, and
    /// every number the analyser reports would be off by the ratio with
    /// nothing to show for it.
    /// </summary>
    [Fact]
    public void A_sweep_defined_at_another_rate_is_refused()
    {
        var signal = new SweepSignal { SampleRate = 44100 };
        Assert.Throws<ArgumentException>(() => new SweepSource(Format, signal, AudioChannel.Stereo));
    }

    [Fact]
    public void An_unknown_channel_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new SweepSource(Format, Short(), "middle"));
    }

    [Fact]
    public void A_sweep_that_would_alias_is_refused_before_it_is_sent()
    {
        var signal = new SweepSignal { EndHz = 30000 };
        Assert.Throws<ArgumentOutOfRangeException>(() => new SweepSource(Format, signal, AudioChannel.Stereo));
    }

    [Fact]
    public void The_description_says_which_speaker_is_being_measured()
    {
        using var source = new SweepSource(Format, null, AudioChannel.Left);
        Assert.Equal("20 Hz–20 kHz sweep, 8 s + 2 s silence (left speaker only)", source.Description);
        Assert.Equal(AudioChannel.Left, source.Channel);
    }

    [Fact]
    public void A_length_that_is_not_whole_frames_is_refused()
    {
        using var source = new SweepSource(Format, Short(), AudioChannel.Stereo);
        Assert.Throws<ArgumentException>(() => source.ReadFrames(new float[7]));
    }
}

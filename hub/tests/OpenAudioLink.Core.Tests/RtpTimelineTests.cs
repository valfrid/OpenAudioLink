using OpenAudioLink.Core.Audio;
using Xunit;

namespace OpenAudioLink.Core.Tests;

public class RtpTimelineTests
{
    private const uint Ssrc = 0xDEADBEEF;

    [Fact]
    public void A_clean_run_writes_every_packet_and_invents_nothing()
    {
        var t = new RtpTimeline();
        for (int i = 0; i < 500; i++)
        {
            var step = t.Accept((ushort)(1000 + i), Ssrc);
            Assert.True(step.Write);
            Assert.Equal(0, step.SilenceFrames);
        }
        Assert.Equal(500, t.Written);
        Assert.Equal(0, t.SilenceFrames);
        Assert.Equal(0, t.SilenceFraction);
    }

    /// <summary>
    /// The property the whole class exists for. Without it a lost packet
    /// shortens the file, and every time taken from that file is wrong by
    /// 5 ms per packet with nothing to indicate it.
    /// </summary>
    [Fact]
    public void A_gap_is_filled_rather_than_closed()
    {
        var t = new RtpTimeline();
        t.Accept(100, Ssrc);
        var step = t.Accept(104, Ssrc);   // 101, 102, 103 never arrived

        Assert.True(step.Write);
        Assert.Equal(3 * 240, step.SilenceFrames);
        Assert.Equal(3 * 240, t.SilenceFrames);
    }

    [Fact]
    public void The_sequence_counter_wrapping_is_not_a_gap()
    {
        var t = new RtpTimeline();
        t.Accept(65534, Ssrc);
        Assert.Equal(0, t.Accept(65535, Ssrc).SilenceFrames);
        Assert.Equal(0, t.Accept(0, Ssrc).SilenceFrames);
        Assert.Equal(0, t.Accept(1, Ssrc).SilenceFrames);
        Assert.Equal(0, t.SilenceFrames);
        Assert.Equal(4, t.Written);
    }

    /// <summary>
    /// Its place in the file is already filled, and the write head only
    /// moves forward — so a late packet is counted and dropped rather than
    /// written over audio that is already correct.
    /// </summary>
    [Fact]
    public void A_late_packet_is_counted_and_not_written()
    {
        var t = new RtpTimeline();
        t.Accept(100, Ssrc);
        t.Accept(105, Ssrc);

        var step = t.Accept(102, Ssrc);
        Assert.False(step.Write);
        Assert.Equal(0, step.SilenceFrames);
        Assert.Equal(1, t.Late);
    }

    [Fact]
    public void The_same_packet_twice_is_a_duplicate_not_a_gap()
    {
        var t = new RtpTimeline();
        t.Accept(100, Ssrc);
        t.Accept(101, Ssrc);

        var step = t.Accept(101, Ssrc);
        Assert.False(step.Write);
        Assert.Equal(1, t.Duplicates);
        Assert.Equal(0, t.SilenceFrames);
    }

    /// <summary>
    /// Two senders' counters are unrelated, so the difference between them
    /// is not a gap. Filling it would invent hours of silence.
    /// </summary>
    [Fact]
    public void A_new_source_rebaselines_instead_of_inventing_silence()
    {
        var t = new RtpTimeline();
        t.Accept(100, Ssrc);
        var step = t.Accept(40000, Ssrc + 1);

        Assert.True(step.Write);
        Assert.Equal(0, step.SilenceFrames);
        Assert.Equal(1, t.SsrcChanges);
        Assert.Equal(0, t.SilenceFrames);
    }

    /// <summary>
    /// A reader has to be able to tell how much of what they are looking at
    /// is real. Half a recording of substituted silence is still a file,
    /// and still useless for a measurement.
    /// </summary>
    [Fact]
    public void How_much_was_invented_is_reported()
    {
        var t = new RtpTimeline();
        t.Accept(0, Ssrc);
        t.Accept(2, Ssrc);   // one missing: 240 invented against 2 x 240 written

        Assert.Equal(2, t.Written);
        Assert.Equal(240, t.SilenceFrames);
        Assert.Equal(1.0 / 3.0, t.SilenceFraction, 6);
    }
}

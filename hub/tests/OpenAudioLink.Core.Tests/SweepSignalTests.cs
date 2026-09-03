using OpenAudioLink.Core.Audio;
using Xunit;

namespace OpenAudioLink.Core.Tests;

public class SweepSignalTests
{
    private static readonly SweepSignal Default = new();

    [Fact]
    public void A_cycle_is_the_sweep_plus_its_silence()
    {
        Assert.Equal(8 * 48000, Default.SweepFrames);
        Assert.Equal(2 * 48000, Default.SilenceFrames);
        Assert.Equal(10 * 48000, Default.CycleFrames);
        Assert.Equal(10.0, Default.CycleDuration.TotalSeconds, 6);
    }

    /// <summary>
    /// The property the analyser folds on. Averaging cycles is what lifts a
    /// −91 dBFS microphone out of a −63 dBFS room, and it only works if the
    /// signal repeats to the frame — a fraction of a frame of drift per
    /// cycle smears the average into a low-pass filter.
    /// </summary>
    [Fact]
    public void The_signal_repeats_exactly_on_the_cycle()
    {
        int cycle = Default.CycleFrames;
        foreach (int n in new[] { 0, 1, 137, 40_000, 383_999, 400_000, cycle - 1 })
        {
            Assert.Equal(Default.SampleAt(n), Default.SampleAt(n + cycle), 12);
            Assert.Equal(Default.SampleAt(n), Default.SampleAt(n + 7L * cycle), 12);
        }
    }

    /// <summary>
    /// How long somebody has to stand still for. Two cycles more than
    /// asked for, and both are spent: the recording starts at an arbitrary
    /// point in the cycle, so the first aligned cycle may not begin until
    /// one cycle in, and the analyser then discards the one after it.
    /// </summary>
    [Theory]
    [InlineData(6, 80)]
    [InlineData(4, 60)]
    [InlineData(12, 140)]
    [InlineData(1, 60)]     // below the floor, which is four sweeps
    [InlineData(0, 60)]
    public void The_time_to_average_a_number_of_sweeps_is_known_in_advance(int cycles, double seconds)
    {
        Assert.Equal(seconds, Default.TimeToAverage(cycles).TotalSeconds, 6);
    }

    /// <summary>
    /// A negative index is not an error case here: the analyser addresses
    /// the signal relative to a peak it found, and that arithmetic goes
    /// backwards past zero as a matter of course.
    /// </summary>
    [Fact]
    public void Indices_before_zero_wrap_rather_than_falling_off()
    {
        Assert.Equal(Default.SampleAt(Default.CycleFrames - 5), Default.SampleAt(-5), 12);
    }

    [Fact]
    public void The_gap_is_actually_silent()
    {
        for (int n = Default.SweepFrames; n < Default.CycleFrames; n += 997)
        {
            Assert.Equal(0.0, Default.SampleAt(n));
        }
        Assert.Equal(0.0, Default.SampleAt(Default.CycleFrames - 1));
    }

    /// <summary>
    /// Switching a 20 Hz sine on at half scale is a step, and a step
    /// excites every frequency at once — it would arrive in the answer as a
    /// second impulse response with no fixed relationship to the first.
    /// </summary>
    [Fact]
    public void It_starts_and_ends_at_nothing()
    {
        Assert.Equal(0.0, Default.SampleAt(0));
        Assert.True(Math.Abs(Default.SampleAt(10)) < 0.001,
            "the first milliseconds must be inside the fade, not at level");
        Assert.Equal(0.0, Default.Envelope(Default.SweepFrames));
        Assert.True(Math.Abs(Default.SampleAt(Default.SweepFrames - 1)) < 0.01);
    }

    [Fact]
    public void The_fades_reach_full_level_and_stay_there()
    {
        Assert.Equal(1.0, Default.Envelope(48_000), 9);        // 1 s in, past the fade
        Assert.Equal(1.0, Default.Envelope(200_000), 9);
        Assert.Equal(1.0, Default.Envelope(Default.SweepFrames - 480), 9); // at the fade-out edge
    }

    [Fact]
    public void Nothing_ever_exceeds_the_stated_amplitude()
    {
        double peak = 0;
        for (int n = 0; n < Default.CycleFrames; n += 7)
        {
            peak = Math.Max(peak, Math.Abs(Default.SampleAt(n)));
        }
        Assert.True(peak <= Default.Amplitude + 1e-12, $"peak {peak} above {Default.Amplitude}");
        Assert.True(peak > Default.Amplitude * 0.99, "and it should get there");
    }

    /// <summary>
    /// The one piece of arithmetic that decides what the measurement means.
    /// Counting the sine's zero crossings integrates its instantaneous
    /// frequency, so this checks the whole phase law rather than a value at
    /// one point: a wrong exponent, a wrong constant or a linear sweep by
    /// mistake all change this count.
    ///
    /// Counted only up to 5 kHz, where there are still nine samples per
    /// cycle. Near 20 kHz there are two and a half, and zero crossings stop
    /// being a reliable way to count anything.
    /// </summary>
    [Fact]
    public void The_frequency_rises_exponentially()
    {
        const double until = 5000.0;
        double l = Math.Log(Default.EndHz / Default.StartHz);
        double seconds = (double)Default.SweepFrames / Default.SampleRate;

        // Where the sweep passes 5 kHz, and the phase it has accumulated by then.
        int upTo = (int)(Default.SampleRate * seconds * Math.Log(until / Default.StartHz) / l);
        double cycles = Default.StartHz * seconds / l * (until / Default.StartHz - 1.0);

        int crossings = 0;
        double previous = Default.SampleAt(0);
        for (int n = 1; n < upTo; n++)
        {
            double value = Default.SampleAt(n);
            if ((previous < 0) != (value < 0))
            {
                crossings++;
            }
            previous = value;
        }

        Assert.Equal(2.0 * cycles, crossings, 2.0);
    }

    /// <summary>
    /// Half the sweep is spent below 632 Hz. That is the whole reason for a
    /// logarithmic sweep: rooms misbehave in the bottom two octaves, and a
    /// linear sweep would be past 10 kHz by its halfway point.
    /// </summary>
    [Fact]
    public void Half_the_time_goes_to_the_bottom_of_the_band()
    {
        double l = Math.Log(Default.EndHz / Default.StartHz);
        double halfway = Default.StartHz * Math.Exp(0.5 * l);   // geometric mean

        Assert.Equal(632.0, halfway, 1.0);
    }

    [Fact]
    public void A_cycle_can_be_rendered_and_matches_the_sample_function()
    {
        var signal = new SweepSignal { SweepSeconds = 0.05, SilenceSeconds = 0.01, FadeInSeconds = 0.005 };
        var buffer = new double[signal.CycleFrames];
        signal.RenderCycle(buffer);

        for (int n = 0; n < buffer.Length; n += 13)
        {
            Assert.Equal(signal.SampleAt(n), buffer[n], 12);
        }
    }

    [Fact]
    public void A_buffer_that_is_not_a_whole_cycle_is_refused()
    {
        var signal = new SweepSignal { SweepSeconds = 0.05, SilenceSeconds = 0.01, FadeInSeconds = 0.005 };
        Assert.Throws<ArgumentException>(() => signal.RenderCycle(new double[signal.CycleFrames - 1]));
    }

    [Theory]
    [InlineData(0.0, 20000.0, 48000)]      // no start frequency
    [InlineData(20.0, 20.0, 48000)]        // does not rise
    [InlineData(20.0, 30000.0, 48000)]     // above Nyquist: it would alias
    public void A_signal_that_cannot_be_measured_with_is_refused(double from, double to, int rate)
    {
        var signal = new SweepSignal { StartHz = from, EndHz = to, SampleRate = rate };
        Assert.Throws<ArgumentOutOfRangeException>(signal.Validate);
    }

    [Fact]
    public void Fades_that_do_not_fit_inside_the_sweep_are_refused()
    {
        var signal = new SweepSignal { SweepSeconds = 0.1, FadeInSeconds = 0.09, FadeOutSeconds = 0.05 };
        Assert.Throws<ArgumentOutOfRangeException>(signal.Validate);
    }

    [Fact]
    public void An_amplitude_over_full_scale_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(new SweepSignal { Amplitude = 1.5 }.Validate);
    }

    [Fact]
    public void The_default_is_valid()
    {
        Default.Validate();
        Assert.Equal("20 Hz–20 kHz sweep, 8 s + 2 s silence", Default.ToString());
    }
}

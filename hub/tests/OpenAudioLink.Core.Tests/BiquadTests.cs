using OpenAudioLink.Core.Audio;
using Xunit;

namespace OpenAudioLink.Core.Tests;

public class BiquadTests
{
    private static Biquad Peak(double hz, double q, double gainDb) =>
        new() { FrequencyHz = hz, Q = q, GainDb = gainDb, SampleRate = 48000 };

    /// <summary>
    /// The closed-form magnitude is what draws the predicted curve, and a
    /// wrong one would promise a correction that never happens. Checked
    /// against running the filter, which is the definition it stands for.
    /// </summary>
    [Theory]
    [InlineData(100, 2, 6)]
    [InlineData(1000, 1, -8)]
    [InlineData(63, 4, -10)]
    public void The_formula_agrees_with_running_the_filter(double hz, double q, double gainDb)
    {
        var filter = Peak(hz, q, gainDb);
        var (b0, b1, b2, a1, a2) = filter.Coefficients();

        foreach (double at in new[] { hz / 4, hz / 2, hz, hz * 2, hz * 4, 5000.0 })
        {
            // Drive it with a sine and measure the steady-state amplitude,
            // after enough samples for the transient to have gone.
            //
            // By RMS rather than by the largest sample: at 5 kHz there are
            // only nine samples per cycle, so the biggest one lands up to
            // half a decibel below the real peak. That is a property of
            // looking, not of the filter, and reading it as one had this
            // test failing against a correct formula.
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0, energy = 0;
            int settle = 20000, measure = 20000;
            for (int n = 0; n < settle + measure; n++)
            {
                double x = Math.Sin(2 * Math.PI * at * n / 48000.0);
                double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = x;
                y2 = y1; y1 = y;
                if (n >= settle)
                {
                    energy += y * y;
                }
            }
            double amplitude = Math.Sqrt(2.0 * energy / measure);

            Assert.Equal(filter.MagnitudeDb(at), 20 * Math.Log10(amplitude), 0.05);
        }
    }

    [Fact]
    public void At_its_own_frequency_it_does_exactly_what_it_says()
    {
        Assert.Equal(6.0, Peak(100, 2, 6).MagnitudeDb(100), 3);
        Assert.Equal(-9.0, Peak(250, 3, -9).MagnitudeDb(250), 3);
    }

    [Fact]
    public void Far_from_it_it_does_nothing()
    {
        var filter = Peak(100, 4, 10);
        Assert.Equal(0.0, filter.MagnitudeDb(20), 0.5);
        Assert.Equal(0.0, filter.MagnitudeDb(4000), 0.5);
    }

    /// <summary>
    /// Bandwidth is f0/Q, so at the half-power points either side of a
    /// peaking filter it should be doing half its gain in dB.
    /// </summary>
    [Fact]
    public void Q_sets_how_wide_it_reaches()
    {
        var narrow = Peak(100, 8, 8);
        var wide = Peak(100, 1, 8);

        // Both do their full 8 dB at the centre.
        Assert.Equal(8.0, narrow.MagnitudeDb(100), 0.05);
        Assert.Equal(8.0, wide.MagnitudeDb(100), 0.05);

        // Half an octave away they have parted company entirely. This is
        // the property the fitter's "refuse anything above Q 8" rule rests
        // on: such a filter reaches almost nowhere.
        Assert.True(wide.MagnitudeDb(141) > narrow.MagnitudeDb(141) + 2,
            $"wide {wide.MagnitudeDb(141):0.0} dB should still reach where "
            + $"narrow {narrow.MagnitudeDb(141):0.0} dB does not");
        Assert.True(narrow.MagnitudeDb(200) < 1.0,
            "an octave away a Q of 8 should be doing essentially nothing");
    }

    [Fact]
    public void A_filter_with_no_gain_is_a_wire()
    {
        var flat = Peak(100, 2, 0);
        foreach (double hz in new[] { 20.0, 100.0, 1000.0, 10000.0 })
        {
            Assert.Equal(0.0, flat.MagnitudeDb(hz), 6);
        }
    }

    [Fact]
    public void It_reads_as_what_it_is()
    {
        Assert.Equal("100 Hz, Q 2.00, +6.0 dB", Peak(100, 2, 6).ToString());
        Assert.Equal("63.5 Hz, Q 1.50, -8.0 dB", Peak(63.5, 1.5, -8).ToString());
    }
}

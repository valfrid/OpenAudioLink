using OpenAudioLink.Core.Audio;
using Xunit;

namespace OpenAudioLink.Core.Tests;

/// <summary>
/// The analyser is checked against rooms whose answer is known: a filter
/// with a shape written down in advance, applied to the sweep, delayed and
/// buried in noise. Anything that comes back other than that shape is a
/// fault in the analysis rather than in a room, which is the only way to
/// tell the two apart without a calibrated laboratory.
/// </summary>
public class SweepAnalyserTests
{
    private const int Rate = 48000;

    /// <summary>Short enough to run in a test, the same shape as the real one.</summary>
    private static SweepSignal Signal() => new()
    {
        StartHz = 100,
        EndHz = 10000,
        SweepSeconds = 0.2,
        SilenceSeconds = 0.15,
        FadeInSeconds = 0.01,
        FadeOutSeconds = 0.004,
        SampleRate = Rate,
    };

    private static SweepAnalysisOptions Options() => new()
    {
        LowHz = 200,
        HighHz = 8000,
        WindowSeconds = 0.08,
        PreWindowSeconds = 0.002,
        AlignMarginSeconds = 0.02,
        ReferenceLowHz = 2000,
        ReferenceHighHz = 6000,
        Points = 200,
    };

    /// <summary>
    /// A peaking filter, RBJ's cookbook. This is the room, for testing:
    /// its shape is written down in advance, so the analyser either finds
    /// it or is wrong.
    /// </summary>
    private static double[] Filtered(double[] input, double frequency, double q, double gainDb)
    {
        double a = Math.Pow(10.0, gainDb / 40.0);
        double w = 2.0 * Math.PI * frequency / Rate;
        double alpha = Math.Sin(w) / (2.0 * q);
        double cos = Math.Cos(w);

        double b0 = 1 + alpha * a, b1 = -2 * cos, b2 = 1 - alpha * a;
        double a0 = 1 + alpha / a, a1 = -2 * cos, a2 = 1 - alpha / a;
        b0 /= a0; b1 /= a0; b2 /= a0; a1 /= a0; a2 /= a0;

        var output = new double[input.Length];
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int n = 0; n < input.Length; n++)
        {
            double y = b0 * input[n] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = input[n];
            y2 = y1; y1 = y;
            output[n] = y;
        }
        return output;
    }

    /// <summary>
    /// The sweep, repeated, put through a room and delayed, with the
    /// recording starting at an arbitrary moment — which is what pressing
    /// record produces.
    /// </summary>
    private static double[] Recording(
        SweepSignal signal, int cycles, int delaySamples, int startOffset,
        double frequency = 0, double q = 1, double gainDb = 0, double noise = 0, int seed = 1)
    {
        int length = cycles * signal.CycleFrames + delaySamples + startOffset;
        var played = new double[length];
        for (int n = 0; n < length; n++)
        {
            played[n] = n >= startOffset + delaySamples
                ? signal.SampleAt(n - startOffset - delaySamples)
                : 0.0;
        }

        var heard = gainDb == 0 ? played : Filtered(played, frequency, q, gainDb);

        if (noise > 0)
        {
            var random = new Random(seed);
            for (int n = 0; n < heard.Length; n++)
            {
                heard[n] += noise * (random.NextDouble() * 2 - 1);
            }
        }
        return heard;
    }

    [Fact]
    public void An_empty_room_gives_a_flat_answer()
    {
        var signal = Signal();
        var recording = Recording(signal, cycles: 6, delaySamples: 300, startOffset: 5000);

        var response = SweepAnalyser.Analyse(recording, signal, Options());

        for (int p = 0; p < response.FrequenciesHz.Count; p++)
        {
            double f = response.FrequenciesHz[p];
            if (f < 300 || f > 6000)
            {
                continue;   // the band edges are where the sweep runs out
            }
            Assert.True(Math.Abs(response.MagnitudeDb[p]) < 0.5,
                $"{f:0} Hz came back at {response.MagnitudeDb[p]:0.00} dB from a room that does nothing");
        }
        Assert.Empty(response.Warnings);
    }

    /// <summary>
    /// The measurement this whole feature exists to make. A +8 dB bump at
    /// 1 kHz is put into the room, and the analyser has to find it there
    /// and nowhere else.
    /// </summary>
    [Fact]
    public void A_peak_in_the_room_comes_back_at_the_right_place_and_size()
    {
        var signal = Signal();
        var recording = Recording(
            signal, cycles: 6, delaySamples: 300, startOffset: 5000,
            frequency: 1000, q: 2, gainDb: 8);

        var response = SweepAnalyser.Analyse(recording, signal, Options());

        Assert.Equal(8.0, At(response, 1000), 1.0);
        Assert.Equal(0.0, At(response, 300), 1.0);
        Assert.Equal(0.0, At(response, 5000), 1.0);
    }

    [Fact]
    public void A_dip_comes_back_as_a_dip()
    {
        var signal = Signal();
        var recording = Recording(
            signal, cycles: 6, delaySamples: 300, startOffset: 5000,
            frequency: 500, q: 1.5, gainDb: -10);

        var response = SweepAnalyser.Analyse(recording, signal, Options());

        Assert.Equal(-10.0, At(response, 500), 1.5);
        Assert.Equal(0.0, At(response, 4000), 1.0);
    }

    /// <summary>
    /// The recording begins whenever somebody pressed the button, and the
    /// answer must not depend on that. Folding at the wrong phase rotates
    /// the response instead of delaying it, and no division undoes a
    /// rotation.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(731)]
    [InlineData(9_000)]
    [InlineData(16_000)]
    public void Where_the_recording_starts_does_not_change_the_answer(int startOffset)
    {
        var signal = Signal();
        var recording = Recording(
            signal, cycles: 6, delaySamples: 300, startOffset: startOffset,
            frequency: 1000, q: 2, gainDb: 8);

        var response = SweepAnalyser.Analyse(recording, signal, Options());

        Assert.Equal(8.0, At(response, 1000), 1.0);
        Assert.Equal(0.0, At(response, 4000), 1.0);
    }

    /// <summary>
    /// The reason the signal repeats. Noise is uncorrelated between cycles
    /// and the sweep is not, so more cycles is a better measurement — and
    /// that has to be true of this implementation and not only of the
    /// theory.
    /// </summary>
    [Fact]
    public void More_cycles_is_a_better_measurement()
    {
        var signal = Signal();
        var options = Options();

        double ErrorWith(int cycles)
        {
            var recording = Recording(
                signal, cycles, delaySamples: 300, startOffset: 4000,
                // As loud as the sweep's own peak, so the answer really is
                // buried and averaging is the only thing that recovers it.
                frequency: 1000, q: 2, gainDb: 8, noise: 0.5, seed: 7);
            var response = SweepAnalyser.Analyse(recording, signal, options);

            double error = 0;
            int count = 0;
            for (int p = 0; p < response.FrequenciesHz.Count; p++)
            {
                double f = response.FrequenciesHz[p];
                if (f is < 3000 or > 7000)
                {
                    continue;   // away from the peak, where the answer is 0 dB
                }
                error += Math.Abs(response.MagnitudeDb[p]);
                count++;
            }
            return error / count;
        }

        double few = ErrorWith(3);
        double many = ErrorWith(24);

        // Eleven times as many looks is 3.4 times the amplitude
        // signal-to-noise ratio in theory. Half is a floor that says the
        // averaging is real without asserting the textbook.
        Assert.True(many < few * 0.5,
            $"averaging 23 sweeps ({many:0.000} dB of error) should beat averaging 2 ({few:0.000} dB) "
            + "by a good margin");
    }

    [Fact]
    public void The_cycles_that_were_averaged_are_reported()
    {
        var signal = Signal();
        var recording = Recording(signal, cycles: 6, delaySamples: 300, startOffset: 0);

        var response = SweepAnalyser.Analyse(recording, signal, Options());

        // One cycle is skipped at the front, and the offset eats part of
        // the last, so this is "at least four and not more than six".
        Assert.InRange(response.CyclesAveraged, 4, 6);
        Assert.Equal(Rate, response.SampleRate);
    }

    /// <summary>
    /// The direct sound belongs at the alignment margin. Reporting where it
    /// actually landed is what makes a failed alignment visible instead of
    /// arriving as a strange curve.
    /// </summary>
    [Fact]
    public void The_direct_sound_lands_where_the_alignment_put_it()
    {
        var signal = Signal();
        var options = Options();
        var recording = Recording(signal, cycles: 6, delaySamples: 300, startOffset: 5000);

        var response = SweepAnalyser.Analyse(recording, signal, options);

        Assert.Equal(0.02, response.AlignMarginSeconds, 3);
        Assert.Equal(response.AlignMarginSeconds, response.ImpulsePeakSeconds, 2);
    }

    [Fact]
    public void A_noisy_room_says_so()
    {
        var signal = Signal();
        var quiet = SweepAnalyser.Analyse(
            Recording(signal, 6, 300, 4000, noise: 0.0001), signal, Options());
        var loud = SweepAnalyser.Analyse(
            Recording(signal, 6, 300, 4000, noise: 0.3), signal, Options());

        Assert.True(quiet.SignalToNoiseDb > loud.SignalToNoiseDb + 20,
            $"a quiet room ({quiet.SignalToNoiseDb:0} dB) should measure better than a loud one "
            + $"({loud.SignalToNoiseDb:0} dB)");
        Assert.Contains(loud.Warnings, w => w.Contains("noise"));
        Assert.Empty(quiet.Warnings);
    }

    /// <summary>
    /// A clipped sweep measures the clipping, and the curve looks like a
    /// real one. It has to be said out loud.
    /// </summary>
    [Fact]
    public void Clipping_is_reported_rather_than_measured()
    {
        var signal = Signal();
        var recording = Recording(signal, cycles: 6, delaySamples: 300, startOffset: 4000);
        for (int n = 0; n < recording.Length; n++)
        {
            recording[n] = Math.Clamp(recording[n] * 4, -1.0, 1.0);
        }

        var response = SweepAnalyser.Analyse(recording, signal, Options());

        Assert.True(response.ClippedSamples > 0);
        Assert.Equal(0.0, response.PeakDbFs, 1);
        Assert.Contains(response.Warnings, w => w.Contains("full scale"));
    }

    [Fact]
    public void A_recording_too_short_to_measure_is_refused()
    {
        var signal = Signal();
        var recording = new double[signal.CycleFrames];

        var ex = Assert.Throws<ArgumentException>(
            () => SweepAnalyser.Analyse(recording, signal, Options()));
        Assert.Contains("two whole cycles", ex.Message);
    }

    [Fact]
    public void The_curve_is_levelled_on_its_reference_band()
    {
        var signal = Signal();
        var recording = Recording(signal, cycles: 6, delaySamples: 300, startOffset: 4000);
        var options = Options();

        var response = SweepAnalyser.Analyse(recording, signal, options);

        double sum = 0;
        int count = 0;
        for (int p = 0; p < response.FrequenciesHz.Count; p++)
        {
            if (response.FrequenciesHz[p] >= options.ReferenceLowHz
                && response.FrequenciesHz[p] <= options.ReferenceHighHz)
            {
                sum += response.MagnitudeDb[p];
                count++;
            }
        }

        Assert.True(count > 0);
        Assert.Equal(0.0, sum / count, 6);
    }

    [Fact]
    public void The_curve_spans_the_band_it_was_asked_for()
    {
        var signal = Signal();
        var recording = Recording(signal, cycles: 6, delaySamples: 300, startOffset: 4000);
        var options = Options();

        var response = SweepAnalyser.Analyse(recording, signal, options);

        Assert.Equal(options.Points, response.FrequenciesHz.Count);
        Assert.Equal(options.Points, response.MagnitudeDb.Count);
        Assert.Equal(options.LowHz, response.FrequenciesHz[0], 6);
        Assert.Equal(options.HighHz, response.FrequenciesHz[^1], 6);

        // Logarithmic: every step is the same ratio, not the same distance.
        double first = response.FrequenciesHz[1] / response.FrequenciesHz[0];
        double last = response.FrequenciesHz[^1] / response.FrequenciesHz[^2];
        Assert.Equal(first, last, 9);
    }

    private static double At(RoomResponse response, double frequency)
    {
        int nearest = 0;
        double best = double.MaxValue;
        for (int p = 0; p < response.FrequenciesHz.Count; p++)
        {
            double distance = Math.Abs(Math.Log(response.FrequenciesHz[p] / frequency));
            if (distance < best)
            {
                best = distance;
                nearest = p;
            }
        }
        return response.MagnitudeDb[nearest];
    }
}

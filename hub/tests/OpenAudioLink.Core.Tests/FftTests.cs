using OpenAudioLink.Core.Audio;
using Xunit;

namespace OpenAudioLink.Core.Tests;

public class FftTests
{
    /// <summary>
    /// The definition the fast transform is an optimisation of. Slow, and
    /// exactly what the answer should be.
    /// </summary>
    private static (double[] Re, double[] Im) DirectTransform(double[] re, double[] im)
    {
        int n = re.Length;
        var outRe = new double[n];
        var outIm = new double[n];
        for (int k = 0; k < n; k++)
        {
            for (int t = 0; t < n; t++)
            {
                double angle = -2.0 * Math.PI * k * t / n;
                double c = Math.Cos(angle);
                double s = Math.Sin(angle);
                outRe[k] += re[t] * c - im[t] * s;
                outIm[k] += re[t] * s + im[t] * c;
            }
        }
        return (outRe, outIm);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(64)]
    [InlineData(256)]
    public void It_agrees_with_the_definition(int n)
    {
        var random = new Random(20260903 + n);
        var re = new double[n];
        var im = new double[n];
        for (int i = 0; i < n; i++)
        {
            re[i] = random.NextDouble() * 2 - 1;
            im[i] = random.NextDouble() * 2 - 1;
        }

        var (wantRe, wantIm) = DirectTransform(re, im);
        Fft.Forward(re, im);

        for (int k = 0; k < n; k++)
        {
            Assert.Equal(wantRe[k], re[k], 9);
            Assert.Equal(wantIm[k], im[k], 9);
        }
    }

    [Fact]
    public void A_round_trip_returns_what_went_in()
    {
        const int n = 4096;
        var random = new Random(4242);
        var re = new double[n];
        var im = new double[n];
        var original = new double[n];
        for (int i = 0; i < n; i++)
        {
            original[i] = re[i] = random.NextDouble() * 2 - 1;
        }

        Fft.Forward(re, im);
        Fft.Inverse(re, im);

        for (int i = 0; i < n; i++)
        {
            Assert.Equal(original[i], re[i], 10);
            Assert.Equal(0.0, im[i], 10);
        }
    }

    /// <summary>
    /// A single bin, so a mistake in the twiddle direction or the bit
    /// reversal shows up as a peak in the wrong place rather than as noise.
    /// </summary>
    [Fact]
    public void A_sinusoid_lands_in_one_bin()
    {
        const int n = 1024;
        const int bin = 37;
        var re = new double[n];
        var im = new double[n];
        for (int i = 0; i < n; i++)
        {
            re[i] = Math.Cos(2.0 * Math.PI * bin * i / n);
        }

        Fft.Forward(re, im);

        for (int k = 0; k < n; k++)
        {
            double magnitude = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            double want = k == bin || k == n - bin ? n / 2.0 : 0.0;
            Assert.Equal(want, magnitude, 6);
        }
    }

    /// <summary>
    /// Multiplying spectra is convolving signals, which is the whole
    /// mechanism the analyser uses to divide a recording by a sweep.
    /// </summary>
    [Fact]
    public void Multiplying_spectra_convolves_the_signals()
    {
        const int n = 16;
        double[] a = [1, 2, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        double[] b = [0, 0, 4, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        var aRe = (double[])a.Clone();
        var aIm = new double[n];
        var bRe = (double[])b.Clone();
        var bIm = new double[n];
        Fft.Forward(aRe, aIm);
        Fft.Forward(bRe, bIm);

        var re = new double[n];
        var im = new double[n];
        for (int k = 0; k < n; k++)
        {
            re[k] = aRe[k] * bRe[k] - aIm[k] * bIm[k];
            im[k] = aRe[k] * bIm[k] + aIm[k] * bRe[k];
        }
        Fft.Inverse(re, im);

        // (1,2,3) * (4,5) starting at offset 2: 4, 13, 22, 15
        Assert.Equal(4.0, re[2], 9);
        Assert.Equal(13.0, re[3], 9);
        Assert.Equal(22.0, re[4], 9);
        Assert.Equal(15.0, re[5], 9);
        Assert.Equal(0.0, re[6], 9);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(480_000, 524_288)]
    [InlineData(524_288, 524_288)]
    public void The_next_power_of_two_is_the_one_at_or_above(int n, int want)
    {
        Assert.Equal(want, Fft.NextPowerOfTwo(n));
    }

    [Fact]
    public void A_length_that_is_not_a_power_of_two_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Fft.Forward(new double[6], new double[6]));
    }

    [Fact]
    public void Mismatched_halves_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Fft.Forward(new double[8], new double[4]));
    }
}

namespace OpenAudioLink.Core.Audio;

/// <summary>
/// An in-place radix-2 complex FFT, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Written out rather than taken from a library because it is forty lines,
/// it is the one piece of arithmetic every number in a room measurement
/// passes through, and a wrong one does not fail — it produces a plausible
/// curve. Tested against a direct discrete Fourier transform, which is the
/// definition this is an optimisation of.
/// </para>
/// <para>
/// Power-of-two lengths only. The alternative is a mixed-radix or Bluestein
/// implementation, which is several times the code for a case that does not
/// arise: the analyser zero-pads to a power of two anyway, and the padding
/// is exact rather than approximate because the room's response is shorter
/// than the sweep's silent gap.
/// </para>
/// </remarks>
public static class Fft
{
    public static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;

    /// <summary>The smallest power of two at least as large as <paramref name="n"/>.</summary>
    public static int NextPowerOfTwo(int n)
    {
        if (n <= 1)
        {
            return 1;
        }
        if (n > 1 << 30)
        {
            throw new ArgumentOutOfRangeException(nameof(n), "No power of two that large fits an int.");
        }

        int size = 1;
        while (size < n)
        {
            size <<= 1;
        }
        return size;
    }

    /// <summary>
    /// Transforms in place. <paramref name="re"/> and <paramref name="im"/>
    /// are the real and imaginary parts of the same array.
    /// </summary>
    public static void Forward(Span<double> re, Span<double> im)
    {
        int n = re.Length;
        if (im.Length != n)
        {
            throw new ArgumentException("The real and imaginary parts must be the same length.", nameof(im));
        }
        if (!IsPowerOfTwo(n))
        {
            throw new ArgumentException($"{n} is not a power of two.", nameof(re));
        }

        // Decimation in time: reorder by bit-reversed index, then combine
        // in blocks of 2, 4, 8 ... n.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }
            j ^= bit;

            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        /*
         * One table of twiddle factors for the whole transform, each entry
         * from its own cosine, indexed with a stride that halves per stage.
         *
         * The textbook alternative advances the factor by repeated complex
         * multiplication, which is faster and drifts: the error compounds
         * once per butterfly, and at 2^19 points it is visible in the
         * fourteenth digit. That is far below anything a 24-bit recording
         * carries, but a transform that is exact costs one array and takes
         * the question off the table.
         */
        int half = n / 2;
        var twiddleRe = new double[Math.Max(half, 1)];
        var twiddleIm = new double[Math.Max(half, 1)];
        for (int k = 0; k < half; k++)
        {
            double angle = -2.0 * Math.PI * k / n;
            twiddleRe[k] = Math.Cos(angle);
            twiddleIm[k] = Math.Sin(angle);
        }

        for (int length = 2; length <= n; length <<= 1)
        {
            int stride = n / length;
            for (int start = 0; start < n; start += length)
            {
                for (int k = 0; k < length / 2; k++)
                {
                    int a = start + k;
                    int b = a + length / 2;
                    double wRe = twiddleRe[k * stride];
                    double wIm = twiddleIm[k * stride];

                    double tRe = re[b] * wRe - im[b] * wIm;
                    double tIm = re[b] * wIm + im[b] * wRe;

                    re[b] = re[a] - tRe;
                    im[b] = im[a] - tIm;
                    re[a] += tRe;
                    im[a] += tIm;
                }
            }
        }
    }

    /// <summary>
    /// The inverse, scaled by 1/n so that a forward followed by an inverse
    /// returns the original.
    /// </summary>
    public static void Inverse(Span<double> re, Span<double> im)
    {
        // conj -> forward -> conj -> scale. Two extra passes over the array
        // in exchange for one implementation of the butterfly.
        for (int i = 0; i < im.Length; i++)
        {
            im[i] = -im[i];
        }

        Forward(re, im);

        double scale = 1.0 / re.Length;
        for (int i = 0; i < re.Length; i++)
        {
            re[i] *= scale;
            im[i] *= -scale;
        }
    }
}

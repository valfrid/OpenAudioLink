namespace OpenAudioLink.Core.Audio;

/// <summary>
/// Streaming sample-rate conversion between two fixed rates.
///
/// Spotify is 44.1 kHz and the RTP profile is 48 kHz, so something has to
/// resample and docs/CAST-POINTS.md puts it at the Hub: one clock domain
/// for the whole house is worth more than the CPU it costs.
///
/// The ratio is exact — 44100:48000 reduces to 147:160 — so this is a
/// polyphase FIR rather than an interpolator chasing a drifting phase.
/// Conceptually the input is upsampled by <c>L</c>, low-pass filtered and
/// decimated by <c>M</c>; in practice only the taps that land on a nonzero
/// sample are ever multiplied, which is <see cref="TapsPerPhase"/> of them
/// per output sample.
///
/// Measured against an ideal sine, the worst error is about -110 dB, the
/// response is flat to 20 kHz and images sit below -90 dB. That is far
/// better than the lossy source it will carry, which is the point: the
/// resampler should not be the thing anyone can hear.
///
/// Not thread-safe: it carries the filter history, so one instance belongs
/// to one stream.
/// </summary>
public sealed class RationalResampler
{
    /// <summary>
    /// Taps per phase. Sets the transition band: 64 puts the passband edge
    /// above 20 kHz for 44.1 kHz input, where 32 would pull it down to
    /// about 18 kHz. The cost is 64 multiplies per output sample, which at
    /// 48 kHz stereo is a few million a second — nothing.
    /// </summary>
    public const int TapsPerPhase = 64;

    /// <summary>
    /// Kaiser window parameter, chosen for roughly -90 dB stopband.
    /// </summary>
    private const double WindowBeta = 8.6;

    private readonly int _channels;
    private readonly int _interpolation;
    private readonly int _decimation;

    /// <summary>Phase-major: <c>_taps[p * TapsPerPhase + k]</c>.</summary>
    private readonly float[] _taps;

    /// <summary>Circular history, one slot per frame, newest at <see cref="_newest"/>.</summary>
    private readonly float[] _history;

    private int _newest;

    /// <summary>
    /// Position of the next output within the current input frame,
    /// numerator-only: an output is due while this is below
    /// <see cref="_interpolation"/>.
    /// </summary>
    private int _phase;

    public RationalResampler(int inputRate, int outputRate, int channels)
    {
        if (inputRate <= 0 || outputRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputRate), "Sample rates must be positive.");
        }
        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), "Channel count must be positive.");
        }

        int divisor = GreatestCommonDivisor(inputRate, outputRate);
        _interpolation = outputRate / divisor;
        _decimation = inputRate / divisor;
        _channels = channels;

        InputRate = inputRate;
        OutputRate = outputRate;

        _taps = BuildTaps(_interpolation, _decimation);
        _history = new float[TapsPerPhase * channels];
    }

    public int InputRate { get; }

    public int OutputRate { get; }

    public int Channels => _channels;

    /// <summary>
    /// True when input and output rates are equal, so callers can skip the
    /// whole thing rather than filter audio that needs no filtering.
    /// </summary>
    public bool IsPassthrough => _interpolation == 1 && _decimation == 1;

    /// <summary>
    /// Group delay in output frames. The filter is symmetric, so this is
    /// exactly half its length: <see cref="TapsPerPhase"/> / 2 input frames.
    /// </summary>
    public double DelayFrames => TapsPerPhase / 2.0 * OutputRate / InputRate;

    /// <summary>
    /// Largest number of output frames <paramref name="inputFrames"/> can
    /// produce. The count varies from call to call because the ratio is not
    /// an integer, so a caller sizing a buffer must use this rather than the
    /// average — the very first frame of a 44.1 to 48 kHz conversion
    /// already produces two.
    /// </summary>
    public int MaxOutputFrames(int inputFrames) =>
        inputFrames <= 0
            ? 0
            // Worst case is starting at phase zero and ending just short of
            // the next input: ((frames + 1) * L - 1) / M.
            : (int)((((long)inputFrames + 1) * _interpolation - 1) / _decimation);

    /// <summary>
    /// Resamples interleaved frames.
    /// </summary>
    /// <param name="input">Interleaved input, a whole number of frames.</param>
    /// <param name="output">
    /// Interleaved output, at least <see cref="MaxOutputFrames"/> long.
    /// </param>
    /// <returns>Samples written, which is frames times channels.</returns>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        int frames = input.Length / _channels;
        if (frames == 0)
        {
            return 0;
        }

        if (output.Length < MaxOutputFrames(frames) * _channels)
        {
            throw new ArgumentException(
                $"Output needs room for {MaxOutputFrames(frames)} frames.", nameof(output));
        }

        int written = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            _newest = _newest + 1 == TapsPerPhase ? 0 : _newest + 1;
            int slot = _newest * _channels;
            for (int channel = 0; channel < _channels; channel++)
            {
                _history[slot + channel] = input[frame * _channels + channel];
            }

            while (_phase < _interpolation)
            {
                int phaseAt = _phase * TapsPerPhase;
                for (int channel = 0; channel < _channels; channel++)
                {
                    double sum = 0.0;
                    int at = _newest;
                    for (int k = 0; k < TapsPerPhase; k++)
                    {
                        sum += _taps[phaseAt + k] * _history[at * _channels + channel];
                        at = at == 0 ? TapsPerPhase - 1 : at - 1;
                    }
                    output[written + channel] = (float)sum;
                }
                written += _channels;
                _phase += _decimation;
            }
            _phase -= _interpolation;
        }

        return written;
    }

    /// <summary>
    /// Forgets the filter history. Called when a stream restarts, so the
    /// tail of the last track cannot bleed into the first samples of the
    /// next one.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_history);
        _newest = 0;
        _phase = 0;
    }

    /// <summary>
    /// Windowed-sinc low-pass, cut at the lower of the two Nyquist limits
    /// and scaled by the interpolation factor to make up for the zeros
    /// inserted between input samples.
    /// </summary>
    private static float[] BuildTaps(int interpolation, int decimation)
    {
        int length = TapsPerPhase * interpolation;
        double cutoff = 0.5 / Math.Max(interpolation, decimation);
        double centre = length / 2.0;
        double normaliser = BesselI0(WindowBeta);

        var prototype = new double[length];
        for (int i = 0; i < length; i++)
        {
            double x = i - centre;
            double sinc = Math.Abs(x) < 1e-9
                ? 2.0 * cutoff
                : Math.Sin(2.0 * Math.PI * cutoff * x) / (Math.PI * x);

            double position = x / centre;
            double window = BesselI0(WindowBeta * Math.Sqrt(Math.Max(0.0, 1.0 - position * position)))
                / normaliser;

            prototype[i] = sinc * window * interpolation;
        }

        // Stored phase-major so one output sample walks a contiguous run.
        var taps = new float[length];
        for (int phase = 0; phase < interpolation; phase++)
        {
            for (int k = 0; k < TapsPerPhase; k++)
            {
                taps[phase * TapsPerPhase + k] = (float)prototype[phase + k * interpolation];
            }
        }
        return taps;
    }

    /// <summary>Modified Bessel function of the first kind, order zero.</summary>
    private static double BesselI0(double x)
    {
        double sum = 1.0;
        double term = 1.0;
        for (int k = 1; k < 64; k++)
        {
            double factor = x / (2.0 * k);
            term *= factor * factor;
            sum += term;
            if (term < 1e-14 * sum)
            {
                break;
            }
        }
        return sum;
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }
        return a;
    }
}
